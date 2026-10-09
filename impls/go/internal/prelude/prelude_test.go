package prelude

// prelude 一致性测试（v1.0 Gate D1）：单一来源的数据表自身无冲突，
// 且被两个解释器消费后产生语义等价的预载值。

import "testing"

func Test预载数据表自洽(t *testing.T) {
	enums := Enums()

	ctors := Ctors()
	for name, ci := range ctors {
		ed, ok := enums[ci.Enum]
		if !ok {
			t.Fatalf("构造器 %s 指向未定义 enum %s", name, ci.Enum)
		}

		if ci.Index >= len(ed.Cases) {
			t.Fatalf("构造器 %s 的 case 序号越界", name)
		}

		if ed.Cases[ci.Index] != name {
			t.Fatalf("构造器 %s 与 case 序不符: cases[%d]=%s", name, ci.Index, ed.Cases[ci.Index])
		}
	}

	ed := ErrorDef()
	if len(ed.Fields) != 1 || ed.Fields[0] != ErrorField {
		t.Fatalf("error struct 字段不符: %v", ed.Fields)
	}
}

func TestOptionResultCase序契约(t *testing.T) {
	if got := Enums()[OptionName].Cases; got[0] != "Some" || got[1] != "None" {
		t.Fatalf("Option case 序漂移: %v（契约：Some=0 None=1）", got)
	}

	if got := Enums()[ResultName].Cases; got[0] != "Ok" || got[1] != "Err" {
		t.Fatalf("Result case 序漂移: %v（契约：Ok=0 Err=1）", got)
	}
}
