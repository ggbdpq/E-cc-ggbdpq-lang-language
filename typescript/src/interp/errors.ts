// 运行时错误（无行号前缀的消息由抛出方负责格式化；CLI 以「错误: <msg>」输出）

export class RtError extends Error {
  constructor(msg: string) {
    super(msg);
    this.name = "RtError";
  }
}
