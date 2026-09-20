// Package version 是 lang-go 的唯一版本源（specs/versioning.md §1）：
// CLI `lang-go version`、Agent Contract 的 toolVersion、发布 tag 三者同源。
package version

// Version 当前工具版本（发布时与 git tag 一致）
const Version = "1.0.0"
