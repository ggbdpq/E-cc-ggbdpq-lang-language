package pipeline_test

import (
	"fmt"
	"io"
	"lang-go/internal/pipeline"
	"testing"
)

func TestDiagnosticsPreservesWrappedErrors(t *testing.T) {
	t.Parallel()

	want := pipeline.ErrorList{{Code: "E1001", Msg: "invalid source", Line: 7}}

	tests := []struct {
		name      string
		err       error
		wantCount int
	}{
		{name: "direct", err: want, wantCount: 1},
		{name: "wrapped", err: fmt.Errorf("compile source: %w", want), wantCount: 1},
		{name: "unrelated", err: io.EOF},
		{name: "nil"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			got := pipeline.Diagnostics(tt.err)
			if len(got) != tt.wantCount {
				t.Fatalf("Diagnostics() count = %d, want %d", len(got), tt.wantCount)
			}

			if len(got) > 0 && got[0] != want[0] {
				t.Fatalf("Diagnostics() = %v, want %v", got, want)
			}
		})
	}
}
