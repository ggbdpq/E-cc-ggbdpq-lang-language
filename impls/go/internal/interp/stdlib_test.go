package interp_test

import (
	"encoding/json"
	"errors"
	"io"
	"lang-go/internal/interp"
	"math"
	"testing"
)

func TestJSONParsePreservesSyntaxError(t *testing.T) {
	t.Parallel()

	tests := []struct{ name, source string }{
		{name: "invalid_object", source: "{]"},
		{name: "invalid_value", source: "?"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			_, found, err := interp.New(io.Discard).CallBuiltin("jsonParse", []interp.Value{interp.StrVal(tt.source)})
			if !found {
				t.Fatal("jsonParse builtin not registered")
			}

			if _, ok := errors.AsType[*json.SyntaxError](err); !ok {
				t.Fatalf("jsonParse() error = %v, want wrapped *json.SyntaxError", err)
			}
		})
	}
}

func TestJSONStringifyPreservesValueError(t *testing.T) {
	t.Parallel()

	tests := []struct {
		name  string
		value float64
	}{
		{name: "nan", value: math.NaN()},
		{name: "positive_infinity", value: math.Inf(1)},
		{name: "negative_infinity", value: math.Inf(-1)},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			_, found, err := interp.New(io.Discard).CallBuiltin("jsonStringify", []interp.Value{interp.FloatVal(tt.value)})
			if !found {
				t.Fatal("jsonStringify builtin not registered")
			}

			if _, ok := errors.AsType[*json.UnsupportedValueError](err); !ok {
				t.Fatalf("jsonStringify() error = %v, want wrapped *json.UnsupportedValueError", err)
			}
		})
	}
}
