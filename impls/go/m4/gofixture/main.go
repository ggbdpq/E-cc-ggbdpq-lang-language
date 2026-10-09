// gofixture —— linkpull bilibili 包原版实现的逐字拷贝 + fixture 打印器。
// 用途：M4 移植的对照真值。本目录下 main.langGo 经解释器/→Go 编译执行的输出
// 须与本程序的输出（除 K 行外）逐字节一致。
// 拷贝来源：vibe/tools/linkpull/internal/extract/bilibili/{md5.go,wbi.go}
// （仅去掉 Bilibili 结构体相关的方法与网络逻辑，纯函数原样保留）。
package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"math/bits"
	"net/url"
	"strconv"
	"strings"
)

func unmarshal(data []byte, v any) error { return json.Unmarshal(data, v) }

// ———— md5.go 原样拷贝 ————

var md5K [64]uint32

func init() {
	for i := range md5K {
		md5K[i] = uint32(math.Floor(math.Abs(math.Sin(float64(i+1))) * (1 << 32)))
	}
}

var md5Shift = [64]uint{
	7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
	5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
	4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
	6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
}

func md5Sum(data []byte) [16]byte {
	msgLen := len(data)
	padded := make([]byte, 0, msgLen+72)
	padded = append(padded, data...)
	padded = append(padded, 0x80)
	for len(padded)%64 != 56 {
		padded = append(padded, 0)
	}
	var bitLen uint64 = uint64(msgLen) * 8
	for i := 0; i < 8; i++ {
		padded = append(padded, byte(bitLen>>(8*uint(i))))
	}

	a0, b0, c0, d0 := uint32(0x67452301), uint32(0xefcdab89), uint32(0x98badcfe), uint32(0x10325476)

	for off := 0; off < len(padded); off += 64 {
		var m [16]uint32
		for j := 0; j < 16; j++ {
			m[j] = uint32(padded[off+4*j]) | uint32(padded[off+4*j+1])<<8 |
				uint32(padded[off+4*j+2])<<16 | uint32(padded[off+4*j+3])<<24
		}
		a, b, c, d := a0, b0, c0, d0
		for i := 0; i < 64; i++ {
			var f, g uint32
			switch {
			case i < 16:
				f = (b & c) | (^b & d)
				g = uint32(i)
			case i < 32:
				f = (d & b) | (^d & c)
				g = uint32(5*i+1) % 16
			case i < 48:
				f = b ^ c ^ d
				g = uint32(3*i+5) % 16
			default:
				f = c ^ (b | ^d)
				g = uint32(7*i) % 16
			}
			f += a + md5K[i] + m[g]
			a, d, c = d, c, b
			b += bits.RotateLeft32(f, int(md5Shift[i]))
		}
		a0 += a
		b0 += b
		c0 += c
		d0 += d
	}

	var digest [16]byte
	putLE := func(dst []byte, v uint32) {
		dst[0] = byte(v)
		dst[1] = byte(v >> 8)
		dst[2] = byte(v >> 16)
		dst[3] = byte(v >> 24)
	}
	putLE(digest[0:4], a0)
	putLE(digest[4:8], b0)
	putLE(digest[8:12], c0)
	putLE(digest[12:16], d0)
	return digest
}

func md5Hex(s string) string {
	d := md5Sum([]byte(s))
	const hexDigits = "0123456789abcdef"
	out := make([]byte, 32)
	for i, v := range d {
		out[2*i] = hexDigits[v>>4]
		out[2*i+1] = hexDigits[v&0xf]
	}
	return string(out)
}

// ———— wbi.go 原样拷贝（纯函数部分）———

var mixinKeyEncTab = []int{
	46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
	33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
	61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
	36, 20, 34, 44, 52,
}

func keyOf(u string) string {
	u = strings.TrimSuffix(strings.Split(u, "?")[0], ".")
	if i := strings.LastIndex(u, "/"); i >= 0 {
		u = u[i+1:]
	}
	return u
}

func getMixinKey(img, sub string) string {
	raw := img + sub
	var sb strings.Builder
	for i, idx := range mixinKeyEncTab {
		if i >= 32 || idx >= len(raw) {
			break
		}
		sb.WriteByte(raw[idx])
	}
	return sb.String()
}

func signWBI(params url.Values, wts int64, mixinKey string) url.Values {
	p := url.Values{}
	for k, vs := range params {
		for _, v := range vs {
			p.Add(stripSpecial(k), stripSpecial(v))
		}
	}
	p.Set("wts", strconv.FormatInt(wts, 10))
	wRid := md5Hex(p.Encode() + mixinKey)
	p.Set("w_rid", wRid)
	return p
}

func stripSpecial(s string) string {
	return strings.Map(func(r rune) rune {
		switch r {
		case '!', '\'', '(', ')', '*':
			return -1
		}
		return r
	}, s)
}

// ———— video.go / list.go 纯函数逐字拷贝 ————

// dashStream 仅保留纯函数用到的字段（原结构其余字段属网络层）
type dashStream struct {
	Codecs    string `json:"codecs"`
	Codecid   int    `json:"codecid"`
	Bandwidth int64  `json:"bandwidth"`
	Height    int    `json:"height"`
}

// codecName 原样拷贝
func codecName(s dashStream) string {
	switch {
	case strings.HasPrefix(s.Codecs, "avc"), s.Codecid == 7:
		return "h264"
	case strings.HasPrefix(s.Codecs, "hev"), strings.HasPrefix(s.Codecs, "hvc"), s.Codecid == 12:
		return "hevc"
	case strings.HasPrefix(s.Codecs, "av01"), s.Codecid == 13:
		return "av1"
	default:
		return s.Codecs
	}
}

// pickCodec 逻辑原样（方法接收者的 b.CodecPref 改为参数 pref）
func pickCodec(streams []dashStream, pref string) dashStream {
	score := func(s dashStream) int {
		name := codecName(s)
		switch strings.ToLower(pref) {
		case "av1":
			switch name {
			case "av1":
				return 0
			case "hevc":
				return 1
			default:
				return 2
			}
		case "hevc":
			switch name {
			case "hevc":
				return 0
			case "h264":
				return 1
			default:
				return 2
			}
		default: // h264
			switch name {
			case "h264":
				return 0
			case "hevc":
				return 1
			default:
				return 2
			}
		}
	}
	best := streams[0]
	for _, s := range streams[1:] {
		if score(s) < score(best) || (score(s) == score(best) && s.Bandwidth > best.Bandwidth) {
			best = s
		}
	}
	return best
}

// fpsOf 原样拷贝
func fpsOf(fr string) int {
	if fr == "" {
		return 0
	}
	num, den, ok := strings.Cut(fr, "/")
	n, err1 := strconv.ParseFloat(num, 64)
	d := 1.0
	if ok {
		if v, err := strconv.ParseFloat(den, 64); err == nil && v > 0 {
			d = v
		}
	}
	if err1 != nil {
		return 0
	}
	return int(n/d + 0.5)
}

// pageOf 原样拷贝
func pageOf(u *url.URL) int {
	p, _ := strconv.Atoi(u.Query().Get("p"))
	return p
}

// refList 对应 core.Ref 的最小面
type refList struct {
	BVID  string `json:"bvid"`
	Title string `json:"title"`
}

// parseArchives 原样拷贝
func parseArchives(data []byte) ([]refList, error) {
	var list struct {
		Archives []refList `json:"archives"`
	}
	if err := unmarshal(data, &list); err != nil {
		return nil, err
	}
	refs := make([]refList, 0, len(list.Archives))
	for _, a := range list.Archives {
		if a.BVID == "" {
			continue
		}
		refs = append(refs, a)
	}
	return refs, nil
}

// listTarget 与 probeList 头部的路径决策逻辑一致（提取为可测函数）
type listTarget struct{ Mid, Kind, SID string }

func listKindOf(u *url.URL) (listTarget, error) {
	segs := strings.Split(strings.Trim(u.Path, "/"), "/")
	if len(segs) < 2 {
		return listTarget{}, errors.New("不是 UP主列表页链接")
	}
	mid := segs[0]
	var kind, sid string
	if segs[1] == "channel" {
		kind, sid = "season", u.Query().Get("sid")
	} else if len(segs) >= 3 && segs[1] == "lists" {
		sid = segs[2]
		kind = u.Query().Get("type")
		if kind != "series" {
			kind = "season"
		}
	} else {
		return listTarget{}, errors.New("不是合集/列表页链接（支持 channel/collectiondetail 与 lists）")
	}
	if sid == "" || mid == "" {
		return listTarget{}, errors.New("列表链接缺少 id 参数")
	}
	return listTarget{Mid: mid, Kind: kind, SID: sid}, nil
}

// ———— fixture 输出 ————

func main() {
	// K 常量表（供 md5.langGo 内嵌，Go 原版由 init() 现算）
	for i := 0; i < 64; i++ {
		if i%8 == 0 {
			fmt.Println()
		}
		fmt.Printf("%d, ", md5K[i])
	}
	fmt.Println()

	// MD5 RFC 1321 官方向量
	vectors := []string{
		"",
		"a",
		"abc",
		"message digest",
		"abcdefghijklmnopqrstuvwxyz",
		"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789",
		"12345678901234567890123456789012345678901234567890123456789012345678901234567890",
	}
	for _, in := range vectors {
		fmt.Printf("MD5|%s|%s\n", in, md5Hex(in))
	}

	// keyOf 三例（带 query、带尾点、无斜杠）
	for _, u := range []string{
		"https://i0.hdslb.com/bfs/wbi/721eadf611e6e185ab5be6a99a29af84.png",
		"https://i0.hdslb.com/bfs/wbi/abc123.",
		"nokey",
	} {
		fmt.Printf("KEYOF|%s|%s\n", u, keyOf(u))
	}

	// mixin key（API-collect 文档示例密钥）
	img := "721eadf611e6e185ab5be6a99a29af84"
	sub := "63a34e3d5df4b3d30a2bec68fc9a9cd6"
	mixin := getMixinKey(img, sub)
	fmt.Printf("MIXIN|%s\n", mixin)

	// WBI 签名两例：文档示例 + 特殊字符/中文
	p1 := url.Values{}
	p1.Set("foo", "one")
	p1.Set("zab", "two")
	p1.Set("bar", "three")
	s1 := signWBI(p1, 1702204800, mixin)
	fmt.Printf("SIGN|%s|%s\n", s1.Encode(), s1.Get("w_rid"))

	p2 := url.Values{}
	p2.Set("q", "a b+c&d=e!f'g(h)i*j")
	p2.Set("foo", "中文")
	s2 := signWBI(p2, 1700000000, mixin)
	fmt.Printf("SIGN|%s|%s\n", s2.Encode(), s2.Get("w_rid"))

	// codecName
	codecCases := []dashStream{
		{Codecs: "avc1.64001F", Codecid: 0},
		{Codecs: "hev1.1.6.L120.90", Codecid: 0},
		{Codecs: "hvc1.2.4.L153.B0", Codecid: 0},
		{Codecs: "av01.0.08M.08", Codecid: 0},
		{Codecs: "", Codecid: 7},
		{Codecs: "", Codecid: 12},
		{Codecs: "", Codecid: 13},
		{Codecs: "mp4a.40.2", Codecid: 0},
	}
	for _, c := range codecCases {
		fmt.Printf("CODEC|%s,%d|%s\n", c.Codecs, c.Codecid, codecName(c))
	}

	// fpsOf
	for _, fr := range []string{"", "30", "16000/1000", "25000/1000", "abc", "1000/0"} {
		fmt.Printf("FPS|%s|%d\n", fr, fpsOf(fr))
	}

	// pickCodec（三偏好各选一遍）
	streams := []dashStream{
		{Codecs: "avc1.64001F", Codecid: 7, Bandwidth: 1000, Height: 1080},
		{Codecs: "hev1.1.6", Codecid: 12, Bandwidth: 1500, Height: 1080},
		{Codecs: "av01.0.08M", Codecid: 13, Bandwidth: 1200, Height: 1080},
	}
	for _, pref := range []string{"h264", "hevc", "av1", "unknown"} {
		p := pickCodec(streams, pref)
		fmt.Printf("PICK|%s|%s|%d\n", pref, codecName(p), p.Bandwidth)
	}

	// pageOf
	for _, raw := range []string{
		"https://www.bilibili.com/video/BV1xx?p=3",
		"https://www.bilibili.com/video/BV1xx",
		"https://www.bilibili.com/video/BV1xx?p=abc",
	} {
		u, _ := url.Parse(raw)
		fmt.Printf("PAGEOF|%s|%d\n", raw, pageOf(u))
	}

	// parseArchives（输入为 API 的 data 段，与 linkpull 调用约定一致）
	archJSONs := []string{
		`{"archives":[{"bvid":"BV1a","title":"第一集"},{"bvid":"","title":"空的跳过"},{"bvid":"BV1b","title":null}]}`,
		`{}`,
		`{"archives":[]}`,
	}
	for _, js := range archJSONs {
		refs, err := parseArchives([]byte(js))
		if err != nil {
			fmt.Printf("ARCHIVES|ERR|%v\n", err)
			continue
		}
		for _, r := range refs {
			fmt.Printf("ARCHIVES|%s|%s\n", r.BVID, r.Title)
		}
		fmt.Printf("ARCHIVESEND|%d\n", len(refs))
	}

	// listKindOf
	for _, raw := range []string{
		"https://space.bilibili.com/123/channel/collectiondetail?sid=456",
		"https://space.bilibili.com/123/lists/789?type=series",
		"https://space.bilibili.com/123/lists/789?type=season",
		"https://space.bilibili.com/123/lists/789",
		"https://space.bilibili.com/123/channel/collectiondetail",
		"https://space.bilibili.com/onlymid",
		"https://www.bilibili.com/video/BV1xx",
	} {
		u, _ := url.Parse(raw)
		tgt, err := listKindOf(u)
		if err != nil {
			fmt.Printf("LISTKIND|%s|ERR|%v\n", raw, err)
			continue
		}
		fmt.Printf("LISTKIND|%s|%s|%s|%s\n", raw, tgt.Mid, tgt.Kind, tgt.SID)
	}
}
