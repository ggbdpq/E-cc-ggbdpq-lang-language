# 2026-09-25 lang-rs Ownership / Runtime Decisions 模板

Rust 实现完成 Semantic Core 前，必须逐项给出决定。

## Compiler

- SourceText 如何拥有
- Span 如何从 byte offset 映射到 code-point offset
- AST ownership
- Symbol Arena
- SymbolId 稳定范围
- Type interning
- TypeId 设计
- HIR storage
- IR storage
- String interning 是否需要
- Diagnostic 生命周期

## Runtime

- Value 表示
- Heap Arena
- Place 表示
- Reference 表示
- Struct copy
- Slice / Map sharing
- Container element write-back
- Closure CaptureEnv
- Captured mutation
- Task state
- Channel state
- Builtin registry
- Runtime error representation

## 每个高风险结构必须回答

```text
Decision
Semantic Rule
Reason
Alternatives
Ownership Consequence
Performance Consequence
Tests
```

以下方案不可默认使用：

```text
Rc<RefCell<T>>
Arc<Mutex<T>>
Box<dyn Any>
大量 clone
unsafe
Tokio runtime
```
