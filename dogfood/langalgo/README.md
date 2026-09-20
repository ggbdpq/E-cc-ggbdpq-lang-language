# langalgo — lang-go 算法与数据结构库

lang-go 生态的标准算法补充库。全部代码使用 lang-go 编写，
通过 →Go 和 →JS 双后端编译运行，以及解释器直接执行三路验证。

## 模块

| 目录 | 内容 |
|------|------|
| sort/ | 冒泡 / 插入 / 归并 / 快排 / 计数 / 堆排序 / 泛型比较排序 |
| search/ | 线性搜索 / 二分搜索 / 泛型查找 |
| strings/ | 反转 / 回文 / 计数 / split / join / trim / 大小写 / 词频 / 校验和 |
| math/ | gcd / fib / 素数 / clamp / 进制转换 / 矩阵 / 向量 / round2 |
| collections/ | 栈 / 队列 / 最小堆 / 有序插入 / 去重 / 集合运算 / 位集合 |
| encode/ | 凯撒 / ROT13 / RLE / 摩斯 / hex / XOR / Base32 |

## 使用

```bash
lang-go run    # 运行全部自检
lang-go test   # 运行测试套件
lang-go check  # 静态检查
lang-go build  # →Go 产物
lang-go build js  # →JS 产物
lang-go verify    # 全管线验证
```

## 统计

- 66 个 lang-go 源文件
- 6 个功能模块 + main 入口
- 1953 行源代码（不含测试）
- 1782 组断言（run 自检）
- 6 个测试函数（lang-go test）
