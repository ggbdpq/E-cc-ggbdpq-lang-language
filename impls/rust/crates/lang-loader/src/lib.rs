//! 模块装载器：目录即包 + import 展开（MODULE-ROOT-001 .. MODULE-TEST-007）。
//!
//! 行为证据（只读）：`impls/go/internal/load/load.go` 与
//! `impls/typescript/src/loader/loader.ts`。错误消息文本与参考实现对齐。
//!
//! 本波不做语法分析：import 的提取经注入的闭包接口（[`load_program`] 的
//! `imports_of` 参数，输入根相对路径 + 文件源码，输出 [`ImportRef`] 列表）。
//! 解析器接线后由语法层提供该闭包（第二波）——本 crate 现提供真实现
//! [`parse_imports`]（lang-syntax 解析抽 Import），消费方可直接传引用使用。
//!
//! 合并语义（MODULE-MERGE-003）：同目录文件按文件名序；目录装载先完成全部
//! 文件的读取与 done 标记，再按序展开各自 import——被导入内容拼接在导入文件
//! 之前（同目录 import 目标因预读已 done 而跳过，保持文件名序，与参考实现
//! 一致）。文件级输出以「导入先于导入文件」排列；语句级 splice（import 位于
//! 文件中部时）在解析器接线后于语句流上执行。

use std::collections::HashSet;
use std::ffi::OsString;
use std::fmt;
use std::fs;
use std::path::{Component, Path, PathBuf};

/// 一条 import 声明：源码原文路径 + 所在行号（1 基，错误消息用）。
#[derive(Debug, Clone, PartialEq)]
pub struct ImportRef {
    pub path: String,
    pub line: u32,
}

/// 合并程序中的一个文件：模块根相对路径（`/` 分隔）+ 源码。
#[derive(Debug, Clone, PartialEq)]
pub struct LoadedFile {
    pub rel_path: String,
    pub source: String,
}

/// 文件装载信息（依赖查询用）：装载序记录，imports 为源码原文依出现序。
#[derive(Debug, Clone, PartialEq)]
pub struct FileInfo {
    pub path: String,
    pub imports: Vec<String>,
    pub order: usize,
}

/// 装载产物：合并序文件列表 + 文件级信息 + 测试文件列表。
#[derive(Debug, Clone, PartialEq)]
pub struct LoadedProgram {
    /// 最终合并序（被导入先于导入者，同目录按文件名序）
    pub files: Vec<LoadedFile>,
    /// 文件装载信息（依装载序）
    pub infos: Vec<FileInfo>,
    /// `include_tests` 时装载的 `*_test.lang-go` 根相对路径（文件名序）
    pub test_files: Vec<String>,
}

/// 装载错误（消息文本已含位置前缀；输出到 stderr 并以非零码终止，见
/// specs/runner-contract.md——装载错误不是 Diagnostic）。
#[derive(Debug, Clone, PartialEq)]
pub struct LoadError {
    pub message: String,
}

impl fmt::Display for LoadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for LoadError {}

/// import 提取闭包的真实现（解析器接线，[`load_program`] 的 `imports_of` 直接传本函数）。
///
/// 行为与 lang-verify / lang-refactor 各自内联的同形闭包逐字一致：以 `rel` 作
/// 源文本路径解析整文件，抽取顶层 `Import` 语句的路径原文与 1 基行号。解析失败
/// 返回空列表——装载只负责抽 import；解析面错误由消费方的逐文件 parse 阶段报告。
pub fn parse_imports(rel: &str, source: &str) -> Vec<ImportRef> {
    let text = lang_core::SourceText::new(rel, source.to_string());
    match lang_syntax::parse(&text, lang_core::SourceId(0)) {
        Ok(stmts) => stmts
            .iter()
            .filter_map(|s| match s {
                lang_syntax::Stmt::Import { path, span } => Some(ImportRef {
                    path: path.clone(),
                    line: span.start.line,
                }),
                _ => None,
            })
            .collect(),
        Err(_) => Vec::new(),
    }
}

/// 装载 `dir` 为一个合并程序。
///
/// `imports_of`：import 提取闭包（根相对路径, 源码）→ import 列表。
/// `include_tests`：MODULE-TEST-007——为 true 时根目录 `*_test.lang-go` 追加在
/// 主程序之后（文件名序），其 import 同样展开；普通 run 传 false 不装载测试文件。
pub fn load_program(
    dir: &Path,
    include_tests: bool,
    imports_of: &mut dyn FnMut(&str, &str) -> Vec<ImportRef>,
) -> Result<LoadedProgram, LoadError> {
    let root = path_absolute(dir)?;
    let mut loader = Loader {
        root: root.clone(),
        imports_of,
        done: HashSet::new(),
        in_progress: HashSet::new(),
        files: Vec::new(),
        infos: Vec::new(),
    };
    loader.load_dir(&root)?;
    let mut test_files = Vec::new();
    if include_tests {
        for name in list_lang_go(&root, true)? {
            let ap = root.join(&name);
            let raw = loader.load_file(&ap)?;
            loader.expand(&raw.imports, &root)?;
            test_files.push(raw.rel.clone());
            loader.files.push(LoadedFile {
                rel_path: raw.rel,
                source: raw.source,
            });
        }
    }
    Ok(LoadedProgram {
        files: loader.files,
        infos: loader.infos,
        test_files,
    })
}

struct RawFile {
    rel: String,
    source: String,
    imports: Vec<ImportRef>,
}

struct Loader<'a> {
    root: PathBuf,
    imports_of: &'a mut dyn FnMut(&str, &str) -> Vec<ImportRef>,
    /// 已完整装载的路径（菱形去重）；同目录文件额外登记去扩展名键
    /// （import "./x" 的解析键是 <dir>/x，与根装载的 <dir>/x.lang-go 对齐）
    done: HashSet<PathBuf>,
    /// 正在展开的路径（真环检测，MODULE-CYCLE-004）
    in_progress: HashSet<PathBuf>,
    files: Vec<LoadedFile>,
    infos: Vec<FileInfo>,
}

impl Loader<'_> {
    /// 装载目录包（MODULE-ROOT-001：目录即包）。
    /// 两阶段：先读完目录内全部非测试 .lang-go（文件名序）并标记 done，
    /// 再按序展开各文件 import、随后落该文件自身。
    fn load_dir(&mut self, dir: &Path) -> Result<(), LoadError> {
        let names = list_lang_go(dir, false)?;
        let mut raws = Vec::with_capacity(names.len());
        for name in names {
            let ap = dir.join(&name);
            let raw = self.load_file(&ap)?;
            self.done.insert(ap.clone());
            self.done.insert(without_lang_go_ext(&ap));
            raws.push(raw);
        }
        for raw in raws {
            self.expand(&raw.imports, dir)?;
            self.files.push(LoadedFile {
                rel_path: raw.rel,
                source: raw.source,
            });
        }
        Ok(())
    }

    /// 读单个文件并记录 FileInfo。
    fn load_file(&mut self, ap: &Path) -> Result<RawFile, LoadError> {
        let source = fs::read_to_string(ap).map_err(|e| LoadError {
            message: format!("{}: {e}", file_name_of(ap)),
        })?;
        let rel = rel_to_root(&self.root, ap);
        let imports = (self.imports_of)(&rel, &source);
        let order = self.infos.len();
        let paths: Vec<String> = imports.iter().map(|i| i.path.clone()).collect();
        self.infos.push(FileInfo {
            path: rel.clone(),
            imports: paths,
            order,
        });
        Ok(RawFile {
            rel,
            source,
            imports,
        })
    }

    /// 展开 import 序列（MODULE-IMPORT-002/MERGE-003/CYCLE-004）：
    /// 相对 fromDir 解析 "./x" → x/ 目录或 x.lang-go 文件，越根报错，
    /// done 跳过（菱形），in_progress 报循环。
    fn expand(&mut self, imports: &[ImportRef], from_dir: &Path) -> Result<(), LoadError> {
        for imp in imports {
            if !imp.path.starts_with("./") {
                return Err(err_at(
                    imp.line,
                    format!("import 仅支持 \"./相对路径\"（得到 \"{}\"）", imp.path),
                ));
            }
            let target = lexically_clean(&from_dir.join(&imp.path));
            let rel = rel_to_root(&self.root, &target);
            if rel == ".." || rel.starts_with("../") {
                return Err(err_at(
                    imp.line,
                    format!("import {} 逃出模块根目录", imp.path),
                ));
            }
            if self.done.contains(&target) {
                continue; // 菱形导入（含同目录预读）：已装载
            }
            if self.in_progress.contains(&target) {
                return Err(err_at(imp.line, format!("循环 import {}", imp.path)));
            }
            self.in_progress.insert(target.clone());
            if target.is_dir() {
                self.load_dir(&target)?;
            } else {
                let mut os: OsString = target.clone().into_os_string();
                os.push(".lang-go");
                let file_ap = PathBuf::from(os);
                let raw = self.load_file(&file_ap)?;
                let parent = target
                    .parent()
                    .map(Path::to_path_buf)
                    .unwrap_or_else(|| target.clone());
                self.expand(&raw.imports, &parent)?;
                self.files.push(LoadedFile {
                    rel_path: raw.rel,
                    source: raw.source,
                });
            }
            self.done.insert(target.clone());
            self.in_progress.remove(&target);
        }
        Ok(())
    }
}

/// 目录内 `.lang-go` 文件名（`tests=true` 取 `*_test.lang-go`，否则取其余），
/// 文件名序（字节序）——不依赖 readdir 的磁盘序。
fn list_lang_go(dir: &Path, tests: bool) -> Result<Vec<String>, LoadError> {
    let mut out = Vec::new();
    let entries = read_dir_or_err(dir)?;
    for entry in entries {
        let entry = entry.map_err(dir_err(dir))?;
        let is_dir = entry.file_type().map_err(dir_err(dir))?.is_dir();
        if is_dir {
            continue;
        }
        let name = entry.file_name().to_string_lossy().into_owned();
        if !name.ends_with(".lang-go") {
            continue;
        }
        if name.ends_with("_test.lang-go") == tests {
            out.push(name);
        }
    }
    out.sort();
    Ok(out)
}

fn read_dir_or_err(dir: &Path) -> Result<fs::ReadDir, LoadError> {
    fs::read_dir(dir).map_err(dir_err(dir))
}

fn dir_err(dir: &Path) -> impl Fn(std::io::Error) -> LoadError + '_ {
    move |e| LoadError {
        message: format!("{}: {e}", dir.display()),
    }
}

fn err_at(line: u32, msg: String) -> LoadError {
    LoadError {
        message: format!("第{line}行: {msg}"),
    }
}

fn file_name_of(p: &Path) -> String {
    p.file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_else(|| p.to_string_lossy().into_owned())
}

/// 去掉 `.lang-go` 尾扩展（`with_extension("")` 从最后一个点截断，
/// 与 Go `strings.TrimSuffix(ap, ".lang-go")` 等价）。
fn without_lang_go_ext(p: &Path) -> PathBuf {
    p.with_extension("")
}

/// 词法规范化（Go `filepath.Clean(join(from, rel))` 等价，不做符号链接解析）：
/// 消去 `.`，`..` 弹出上一级；绝对路径顶端之上的 `..` 丢弃。
fn lexically_clean(p: &Path) -> PathBuf {
    let absolute = p.is_absolute();
    let mut out = PathBuf::new();
    for comp in p.components() {
        match comp {
            Component::CurDir => {}
            Component::ParentDir => {
                if matches!(out.components().next_back(), Some(Component::Normal(_))) {
                    out.pop();
                } else if !absolute {
                    out.push("..");
                }
            }
            c => out.push(c.as_os_str()),
        }
    }
    out
}

/// 相对 root 的路径文本（`/` 分隔；root 自身为 `.`；越出 root 以 `../` 开头）。
fn rel_to_root(root: &Path, p: &Path) -> String {
    let rc: Vec<Component> = root.components().collect();
    let pc: Vec<Component> = p.components().collect();
    let mut i = 0;
    while i < rc.len() && i < pc.len() && rc[i] == pc[i] {
        i += 1;
    }
    let mut parts: Vec<String> = vec!["..".to_string(); rc.len() - i];
    for comp in &pc[i..] {
        if let Component::Normal(os) = comp {
            parts.push(os.to_string_lossy().into_owned());
        }
    }
    if parts.is_empty() {
        ".".to_string()
    } else {
        parts.join("/")
    }
}

/// `std::path::absolute` 的错误包装（词法绝对化，等价 Go filepath.Abs）。
fn path_absolute(dir: &Path) -> Result<PathBuf, LoadError> {
    std::path::absolute(dir).map_err(|e| LoadError {
        message: format!("{}: {e}", dir.display()),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    /// 测试用 import 提取桩：识别 `import "./x"` 形式的行（不做真语法分析）。
    fn scan_imports(rel: &str, src: &str) -> Vec<ImportRef> {
        let _ = rel;
        src.lines()
            .enumerate()
            .filter_map(|(i, line)| {
                let t = line.trim();
                let rest = t.strip_prefix("import ")?;
                let p = rest.trim().trim_matches('"');
                Some(ImportRef {
                    path: p.to_string(),
                    line: (i + 1) as u32,
                })
            })
            .collect()
    }

    fn load(dir: &Path, tests: bool) -> Result<LoadedProgram, LoadError> {
        load_program(dir, tests, &mut scan_imports)
    }

    /// 临时目录夹具（进程内唯一编号，Drop 时尽力清理）。
    struct TempDir(PathBuf);

    impl TempDir {
        fn new(tag: &str) -> Self {
            static N: AtomicU32 = AtomicU32::new(0);
            let p = std::env::temp_dir().join(format!(
                "lang-loader-{tag}-{}-{}",
                std::process::id(),
                N.fetch_add(1, Ordering::Relaxed)
            ));
            let _ = fs::remove_dir_all(&p);
            fs::create_dir_all(&p).unwrap();
            TempDir(p)
        }

        fn write(&self, rel: &str, content: &str) {
            let ap = self.0.join(rel);
            if let Some(parent) = ap.parent() {
                fs::create_dir_all(parent).unwrap();
            }
            fs::write(ap, content).unwrap();
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn rels(prog: &LoadedProgram) -> Vec<String> {
        prog.files.iter().map(|f| f.rel_path.clone()).collect()
    }

    #[test]
    fn single_dir_files_sorted_by_name() {
        let td = TempDir::new("flat");
        td.write("b.lang-go", "println(2)");
        td.write("a.lang-go", "println(1)");
        td.write("c.lang-go", "println(3)");
        td.write("notes.txt", "非 .lang-go 不装载");
        let prog = load(td.path(), false).unwrap();
        assert_eq!(rels(&prog), vec!["a.lang-go", "b.lang-go", "c.lang-go"]);
        assert_eq!(prog.files[1].source, "println(2)");
    }

    #[test]
    fn dir_package_imported_before_importer() {
        let td = TempDir::new("dirpkg");
        td.write("main.lang-go", "import \"./lib\"\nprintln(1)");
        td.write("lib/y.lang-go", "println(2)");
        td.write("lib/x.lang-go", "println(3)");
        let prog = load(td.path(), false).unwrap();
        // 被导入目录包先于导入者；目录内按文件名序
        assert_eq!(
            rels(&prog),
            vec!["lib/x.lang-go", "lib/y.lang-go", "main.lang-go"]
        );
        // FileInfo 依装载序：main 先读，lib 后读
        assert_eq!(prog.infos[0].path, "main.lang-go");
        assert_eq!(prog.infos[0].imports, vec!["./lib"]);
        assert_eq!(prog.infos[1].path, "lib/x.lang-go");
        assert_eq!(prog.infos[2].path, "lib/y.lang-go");
    }

    #[test]
    fn single_file_import_spliced_before_importer() {
        // import 目标文件不在已预读目录里（子目录直引文件；import 路径不带扩展名，
        // 解析时补 .lang-go——MODULE-IMPORT-002）
        let td = TempDir::new("fileimp");
        td.write("main.lang-go", "import \"./sub/u\"\nprintln(1)");
        td.write("sub/u.lang-go", "println(2)");
        let prog = load(td.path(), false).unwrap();
        assert_eq!(rels(&prog), vec!["sub/u.lang-go", "main.lang-go"]);
    }

    #[test]
    fn transitive_chain_deep_dependency_first() {
        let td = TempDir::new("chain");
        td.write("main.lang-go", "import \"./a\"\nprintln(0)");
        td.write("a/a.lang-go", "import \"./../b\"\nprintln(1)");
        td.write("b/b.lang-go", "println(2)");
        let prog = load(td.path(), false).unwrap();
        assert_eq!(
            rels(&prog),
            vec!["b/b.lang-go", "a/a.lang-go", "main.lang-go"]
        );
    }

    #[test]
    fn diamond_import_deduplicated() {
        // main→a→c、main→b→c：c 只载入一次（MODULE-CYCLE-004）
        let td = TempDir::new("diamond");
        td.write("main.lang-go", "import \"./a\"\nimport \"./b\"\nprintln(0)");
        td.write("a.lang-go", "import \"./c\"\nprintln(1)");
        td.write("b.lang-go", "import \"./c\"\nprintln(2)");
        td.write("c.lang-go", "println(3)");
        let prog = load(td.path(), false).unwrap();
        let all = rels(&prog);
        assert_eq!(all.iter().filter(|p| *p == "c.lang-go").count(), 1);
        assert_eq!(
            all,
            vec!["a.lang-go", "b.lang-go", "c.lang-go", "main.lang-go"]
        );
        assert_eq!(prog.infos.len(), 4); // c 只读一次
    }

    #[test]
    fn same_dir_file_import_keeps_filename_order() {
        // 同目录文件被根目录预读（文件名序在前），import 目标 done → 跳过，
        // 合并序保持文件名序（MODULE-MERGE-003「同目录按文件名序」）
        let td = TempDir::new("samedir");
        td.write("main.lang-go", "import \"./util\"\nprintln(1)");
        td.write("util.lang-go", "println(2)");
        let prog = load(td.path(), false).unwrap();
        assert_eq!(rels(&prog), vec!["main.lang-go", "util.lang-go"]);
    }

    #[test]
    fn dir_cycle_detected() {
        // lib → inner → 回指 lib（lib 尚在展开链上）；消息不带引号（参考实现 %s）
        let td = TempDir::new("cycle");
        td.write("main.lang-go", "import \"./lib\"");
        td.write("lib/s.lang-go", "import \"./inner\"");
        td.write("lib/inner/i.lang-go", "import \"./..\"");
        let err = load(td.path(), false).unwrap_err();
        assert_eq!(err.message, "第1行: 循环 import ./..");
    }

    #[test]
    fn file_cycle_detected() {
        // 经 import 直引文件链 a → b → a（目标目录未预读，真环）
        let td = TempDir::new("filecycle");
        td.write("main.lang-go", "import \"./sub/a\"");
        td.write("sub/a.lang-go", "import \"./b\"");
        td.write("sub/b.lang-go", "import \"./a\"");
        let err = load(td.path(), false).unwrap_err();
        assert_eq!(err.message, "第1行: 循环 import ./a");
    }

    #[test]
    fn escape_root_rejected() {
        let td = TempDir::new("escape");
        let outside = td.path().join("../outside.lang-go");
        fs::write(&outside, "println(9)").unwrap();
        td.write("main.lang-go", "import \"./../outside\"");
        let err = load(td.path(), false).unwrap_err();
        assert_eq!(err.message, "第1行: import ./../outside 逃出模块根目录");
        let _ = fs::remove_file(outside);
    }

    #[test]
    fn non_relative_import_rejected() {
        let td = TempDir::new("nonrel");
        td.write("main.lang-go", "import \"lib\"");
        let err = load(td.path(), false).unwrap_err();
        assert_eq!(
            err.message,
            "第1行: import 仅支持 \"./相对路径\"（得到 \"lib\"）"
        );
    }

    #[test]
    fn missing_import_target_reports_file() {
        let td = TempDir::new("missing");
        td.write("main.lang-go", "import \"./nope\"");
        let err = load(td.path(), false).unwrap_err();
        assert!(err.message.contains("nope.lang-go"), "{}", err.message);
    }

    #[test]
    fn relative_normalization_stays_inside_root() {
        // "./sub/../util" 规范化后仍在根内：util 已被根预读（done）→ 跳过不报错；
        // sub/keep 从未被 import，不装载
        let td = TempDir::new("normalize");
        td.write("main.lang-go", "import \"./sub/../util\"\nprintln(1)");
        td.write("sub/keep.lang-go", "println(2)");
        td.write("util.lang-go", "println(3)");
        let prog = load(td.path(), false).unwrap();
        assert_eq!(rels(&prog), vec!["main.lang-go", "util.lang-go"]);
    }

    #[test]
    fn same_stem_file_preload_shadows_dir_target() {
        // 参考实现的去重键语义：根预读 x.lang-go 时登记去扩展名键 <root>/x，
        // 与目录包目标 <root>/x 撞键 → import "./x" 被当作已装载而跳过，
        // x/ 目录包不装载（x.lang-go 仅按文件名序出现在合并序里）
        let td = TempDir::new("dirwins");
        td.write("main.lang-go", "import \"./x\"");
        td.write("x/inner.lang-go", "println(1)");
        td.write("x.lang-go", "println(2)");
        let prog = load(td.path(), false).unwrap();
        assert_eq!(rels(&prog), vec!["main.lang-go", "x.lang-go"]);
        assert_eq!(prog.infos.len(), 2);
    }

    #[test]
    fn test_files_only_loaded_when_requested() {
        let td = TempDir::new("tests");
        td.write("main.lang-go", "println(1)");
        td.write("z_test.lang-go", "fn testZ() {}");
        td.write("a_test.lang-go", "import \"./lib\"\nfn testA() {}");
        td.write("lib/l.lang-go", "println(2)");

        let run = load(td.path(), false).unwrap();
        assert_eq!(rels(&run), vec!["main.lang-go"]);
        assert!(run.test_files.is_empty());

        let t = load(td.path(), true).unwrap();
        // 测试文件追加在主程序后（文件名序），其 import 同样展开
        assert_eq!(
            rels(&t),
            vec![
                "main.lang-go",
                "lib/l.lang-go",
                "a_test.lang-go",
                "z_test.lang-go"
            ]
        );
        assert_eq!(t.test_files, vec!["a_test.lang-go", "z_test.lang-go"]);
    }

    #[test]
    fn imports_closure_receives_rel_path_and_source() {
        let td = TempDir::new("closure");
        td.write("main.lang-go", "println(1)");
        let mut got: Vec<(String, String)> = Vec::new();
        let prog = load_program(td.path(), false, &mut |rel: &str, src: &str| {
            got.push((rel.to_string(), src.to_string()));
            Vec::new()
        })
        .unwrap();
        assert_eq!(got, vec![("main.lang-go".into(), "println(1)".into())]);
        assert_eq!(prog.files.len(), 1);
    }

    #[test]
    fn rel_paths_use_forward_slashes() {
        // Windows 上 PathBuf 是反斜杠；对外 rel_path 必须是 `/` 分隔
        let td = TempDir::new("slashes");
        td.write("main.lang-go", "import \"./p/q\"");
        td.write("p/q.lang-go", "println(1)");
        let prog = load(td.path(), false).unwrap();
        assert_eq!(rels(&prog), vec!["p/q.lang-go", "main.lang-go"]);
        assert!(!prog.files[0].rel_path.contains('\\'));
    }

    /// 解析器接线：parse_imports 直接作 imports_of 使用（真语法分析抽 import）。
    #[test]
    fn parse_imports_extract_imports_via_syntax() {
        let td = TempDir::new("parseimports");
        td.write("main.lang-go", "import \"./lib\"\nprintln(1)");
        td.write("lib/l.lang-go", "println(2)");
        let prog = load_program(td.path(), false, &mut parse_imports).unwrap();
        assert_eq!(rels(&prog), vec!["lib/l.lang-go", "main.lang-go"]);
        // 解析失败 → 空列表（装载不报错，解析面由消费方报告）
        assert!(parse_imports("x.lang-go", "fn (").is_empty());
        let refs = parse_imports("m.lang-go", "import \"./a\"\nlet x = 1\nimport \"./b\"");
        assert_eq!(refs.len(), 2);
        assert_eq!(refs[0].path, "./a");
        assert_eq!(refs[0].line, 1);
        assert_eq!(refs[1].line, 3);
    }
}
