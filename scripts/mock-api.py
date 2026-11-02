"""Local-only synthetic SSE service for manual UI checks. No external API calls."""
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        size = int(self.headers.get("Content-Length", "0"))
        payload = json.loads(self.rfile.read(size))
        question = payload.get("messages", [{}])[-1].get("content", "")
        delay = 0.5 if "慢速" in question else 0.08
        if self.path != "/v1/chat/completions" or len(payload.get("messages", [])) != 2:
            self.send_error(400)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        answer = "熵是描述一个系统的不确定程度或可能状态数量的量。\n\n直观例子：一枚公平硬币的结果较难预测，信息熵较高；一枚几乎总朝上的硬币，信息熵较低。\n\n在不同领域，熵的定义各有侧重：信息论关注不确定性，热力学关注能量分散与状态数量。"
        try:
            time.sleep(0.4)
            for index in range(0, len(answer), 3):
                chunk = {"choices": [{"delta": {"content": answer[index:index + 3]}}]}
                self.wfile.write(("data: " + json.dumps(chunk, ensure_ascii=False) + "\n\n").encode())
                self.wfile.flush()
                time.sleep(delay)
            final = {"choices": [{"delta": {}, "finish_reason": "stop"}], "usage": {"prompt_tokens": 103, "completion_tokens": 109}}
            self.wfile.write(("data: " + json.dumps(final) + "\n\ndata: [DONE]\n\n").encode())
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass


if __name__ == "__main__":
    print("Synthetic SSE service on 127.0.0.1:53218", flush=True)
    ThreadingHTTPServer(("127.0.0.1", 53218), Handler).serve_forever()
