using System.Security.Cryptography;
using System.Text;

namespace Lang.Edit;

/// <summary>
/// SHA-256 哈希（specs/agent-protocol.md §6：precondition.fileHash = sha256 hex）。
/// 用 BCL 标准实现（System.Security.Cryptography，FIPS 180-4），零外部依赖；
/// hex 为小写（与 Go encoding/hex、Rust hex 证据同形）。输入按 UTF-8 字节度量——
/// 与码点 offset 契约无涉（哈希只对全文取，不涉区间）。
/// </summary>
public static class EditHash
{
    /// <summary>全文 UTF-8 字节的 SHA-256，小写 hex（64 字符）。</summary>
    public static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
