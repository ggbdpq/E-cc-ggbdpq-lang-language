package greet

import "strings"

func Hello(name string) (string, error) {
	return strings.ToUpper(name), nil
}

func Sq(n int64) int64 {
	return n * n
}

func join(parts []string, sep string) string {
	return strings.Join(parts, sep)
}
