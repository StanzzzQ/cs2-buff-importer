#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
BUFF 解析测试器（图形界面）

双击运行 / 命令行运行：
    python tools/buff_gui.py
    pythonw tools/buff_gui.py     （不弹黑框）

只依赖 Python 自带库（tkinter）。真正的解析逻辑在 buff_probe.py，这里只是一个壳。
"""

import json
import os
import queue
import sys
import threading
import tkinter as tk
from tkinter import font as tkfont
from tkinter import messagebox, ttk
from tkinter.scrolledtext import ScrolledText

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import buff_probe  # noqa: E402

CONFIG_PATH = os.path.join(HERE, "gui_config.json")

TITLE = "BUFF 解析测试器"
DEFAULT_PROXY = "http://127.0.0.1:7890"


class QueueWriter:
    """把 print 的输出来塞进队列，交给界面线程消费。"""

    def __init__(self, sink):
        self.sink = sink

    def write(self, text):
        if text:
            self.sink.put(text)
        return len(text)

    def flush(self):
        pass


class App:
    def __init__(self, root):
        self.root = root
        self.queue = queue.Queue()
        self.worker = None

        root.title(TITLE)
        root.geometry("940x720")
        root.minsize(760, 560)

        try:
            tkfont.nametofont("TkDefaultFont").configure(family="Microsoft YaHei UI", size=10)
            tkfont.nametofont("TkTextFont").configure(family="Consolas", size=10)
        except tk.TclError:
            pass

        self._build_ui()
        self._load_config()
        self.root.after(90, self._drain_queue)

    # ------------------------------------------------------------------ UI

    def _build_ui(self):
        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill="both", expand=True)

        form = ttk.Frame(outer)
        form.pack(fill="x")
        form.columnconfigure(1, weight=1)

        ttk.Label(form, text="分享链接").grid(row=0, column=0, sticky="w", pady=4)
        self.link_var = tk.StringVar()
        self.link_entry = ttk.Entry(form, textvariable=self.link_var)
        self.link_entry.grid(row=0, column=1, columnspan=3, sticky="we", padx=(8, 0), pady=4)

        ttk.Label(form, text="Cookie").grid(row=1, column=0, sticky="w", pady=4)
        self.cookie_var = tk.StringVar()
        self.cookie_entry = ttk.Entry(form, textvariable=self.cookie_var, show="•")
        self.cookie_entry.grid(row=1, column=1, sticky="we", padx=(8, 0), pady=4)

        self.show_cookie = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            form, text="显示", variable=self.show_cookie, command=self._toggle_cookie
        ).grid(row=1, column=2, padx=6)

        self.remember_cookie = tk.BooleanVar(value=False)
        ttk.Checkbutton(form, text="记住", variable=self.remember_cookie).grid(row=1, column=3)

        ttk.Label(form, text="代理").grid(row=2, column=0, sticky="w", pady=4)
        self.proxy_var = tk.StringVar(value=DEFAULT_PROXY)
        self.proxy_entry = ttk.Entry(form, textvariable=self.proxy_var)
        self.proxy_entry.grid(row=2, column=1, columnspan=2, sticky="we", padx=(8, 0), pady=4)

        self.use_proxy = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            form, text="启用", variable=self.use_proxy, command=self._toggle_proxy
        ).grid(row=2, column=3, sticky="w", padx=6)

        options = ttk.Frame(outer)
        options.pack(fill="x", pady=(8, 6))

        self.do_page = tk.BooleanVar(value=True)
        self.do_inspect = tk.BooleanVar(value=True)
        ttk.Checkbutton(options, text="测试分享页 var items", variable=self.do_page).pack(
            side="left"
        )
        ttk.Checkbutton(options, text="测试检视链接", variable=self.do_inspect).pack(
            side="left", padx=(12, 0)
        )

        self.run_button = ttk.Button(options, text="开始解析", command=self.start)
        self.run_button.pack(side="right")
        ttk.Button(options, text="清空日志", command=self.clear_log).pack(
            side="right", padx=(0, 8)
        )
        ttk.Button(options, text="复制日志", command=self.copy_log).pack(
            side="right", padx=(0, 8)
        )

        result_box = ttk.LabelFrame(outer, text="解析结果", padding=8)
        result_box.pack(fill="x")

        self.tree = ttk.Treeview(
            result_box, columns=("key", "value"), show="headings", height=8
        )
        self.tree.heading("key", text="字段")
        self.tree.heading("value", text="值")
        self.tree.column("key", width=150, stretch=False, anchor="w")
        self.tree.column("value", width=640, anchor="w")
        self.tree.pack(fill="x")

        log_box = ttk.LabelFrame(outer, text="日志", padding=8)
        log_box.pack(fill="both", expand=True, pady=(10, 0))

        self.log = ScrolledText(
            log_box, height=14, wrap="word", state="disabled", font=("Consolas", 10)
        )
        self.log.pack(fill="both", expand=True)
        self.log.tag_configure("ok", foreground="#1a7f37")
        self.log.tag_configure("err", foreground="#c0392b")
        self.log.tag_configure("warn", foreground="#b7791f")
        self.log.tag_configure("head", foreground="#1f4e79", font=("Consolas", 10, "bold"))

        self.status_var = tk.StringVar(value="就绪")
        ttk.Label(outer, textvariable=self.status_var, anchor="w", padding=(2, 6)).pack(
            fill="x"
        )

        self._toggle_cookie()
        self._toggle_proxy()
        self.link_entry.focus_set()
        self.root.bind("<Return>", lambda _event: self.start())

    def _toggle_cookie(self):
        self.cookie_entry.configure(show="" if self.show_cookie.get() else "•")

    def _toggle_proxy(self):
        state = "normal" if self.use_proxy.get() else "disabled"
        self.proxy_entry.configure(state=state)

    # ------------------------------------------------------------------ 动作

    def start(self):
        if self.worker and self.worker.is_alive():
            return

        link = self.link_var.get().strip()
        cookie = self.cookie_var.get().strip()

        if not link:
            messagebox.showwarning(TITLE, "先把 BUFF 分享链接粘进来")
            return

        if not self.do_page.get() and not self.do_inspect.get():
            messagebox.showwarning(TITLE, "至少勾选一个测试项")
            return

        argv = ["buff_probe.py", link]

        if cookie:
            argv += ["--cookie", cookie]
        else:
            argv += ["--cookie", ""]

        if self.use_proxy.get() and self.proxy_var.get().strip():
            argv += ["--proxy", self.proxy_var.get().strip()]

        if not self.do_page.get():
            argv.append("--skip-page")

        if not self.do_inspect.get():
            argv.append("--skip-inspect")

        self.clear_log()
        self._clear_tree()
        self.run_button.configure(state="disabled")
        self.status_var.set("正在请求 BUFF ……")

        buff_probe.LAST_SHARE = None
        buff_probe.LAST_INSPECT = None

        self.worker = threading.Thread(target=self._run, args=(argv,), daemon=True)
        self.worker.start()

    def _run(self, argv):
        writer = QueueWriter(self.queue)
        original_stdout = sys.stdout
        original_argv = sys.argv
        code = 1

        try:
            sys.stdout = writer
            sys.argv = argv
            code = buff_probe.main()
        except SystemExit as exc:
            code = exc.code if isinstance(exc.code, int) else 1
        except Exception as exc:  # noqa: BLE001
            self.queue.put(f"\n[异常] {type(exc).__name__}: {exc}\n")
            code = 1
        finally:
            sys.stdout = original_stdout
            sys.argv = original_argv
            self.queue.put(("__done__", code))

    def _drain_queue(self):
        try:
            while True:
                item = self.queue.get_nowait()

                if isinstance(item, tuple) and item and item[0] == "__done__":
                    self._finish(item[1])
                    continue

                self._append(item)
        except queue.Empty:
            pass

        self.root.after(90, self._drain_queue)

    def _append(self, text):
        self.log.configure(state="normal")
        buffer = getattr(self, "_partial", "")
        buffer += text

        lines = buffer.split("\n")
        self._partial = lines.pop()

        for line in lines:
            self.log.insert("end", line + "\n", self._tag_for(line))

        self.log.configure(state="disabled")
        self.log.see("end")

    @staticmethod
    def _tag_for(line):
        stripped = line.strip()
        if stripped.startswith("✓"):
            return "ok"
        if stripped.startswith("✗") or stripped.startswith("[失败]"):
            return "err"
        if stripped.startswith("[1/3]") or stripped.startswith("[2/3]") or stripped.startswith("[3/3]"):
            return "head"
        if stripped.startswith("[配置]") or "302 →" in stripped:
            return "warn"
        return ""

    def _finish(self, code):
        if getattr(self, "_partial", ""):
            self.log.configure(state="normal")
            self.log.insert("end", self._partial + "\n", self._tag_for(self._partial))
            self.log.configure(state="disabled")
            self._partial = ""

        self._fill_tree()
        self.run_button.configure(state="normal")

        if buff_probe.LAST_SHARE or buff_probe.LAST_INSPECT:
            self.status_var.set("解析成功")
        else:
            self.status_var.set(f"解析失败（退出码 {code}）")

        self._save_config()

    # ------------------------------------------------------------------ 结果表

    def _clear_tree(self):
        for row in self.tree.get_children():
            self.tree.delete(row)

    def _fill_tree(self):
        self._clear_tree()

        inspect = buff_probe.LAST_INSPECT
        share = buff_probe.LAST_SHARE

        if share:
            self._row("来源", "分享页 var items")
            self._row("名称", share.get("name"))
            self._row("武器", share.get("weapon"))
            self._row("品质", share.get("quality"))
            self._row("涂装", share.get("paintindex"))
            self._row("模板", share.get("paintseed"))
            self._row("磨损", share.get("paintwear"))
            self._row("印花", f"{len(share.get('stickers') or [])} 张")
            self._row("挂件", f"{len(share.get('keychains') or [])} 个")

        if inspect:
            if share:
                self._row("", "")
            self._row("来源", "检视链接 CEconItemPreviewDataBlock")
            self._row("defindex", inspect.get("defindex"))
            self._row("涂装", inspect.get("paintindex"))
            self._row("模板", inspect.get("paintseed"))
            self._row("磨损", f"{inspect.get('paintwear', 0):.10f}")
            self._row("印花", f"{len(inspect.get('stickers') or [])} 张")
            self._row("挂件", f"{len(inspect.get('keychains') or [])} 个")

            for sticker in inspect.get("stickers") or []:
                self._row(
                    f"  印花 slot {sticker['slot']}",
                    "id {id}  wear {wear:.3f}  scale {scale:.2f}  "
                    "offset ({x:+.4f}, {y:+.4f}, {z:+.4f})  rot {rot:.1f}".format(
                        id=sticker["id"],
                        wear=sticker["wear"],
                        scale=sticker["scale"],
                        x=sticker["x"],
                        y=sticker["y"],
                        z=sticker["z"],
                        rot=sticker["rotation"],
                    ),
                )

            if inspect.get("name"):
                self._row("改名", inspect["name"])

        if not share and not inspect:
            self._row("结果", "没有解析到数据，看下面的日志")

    def _row(self, key, value):
        self.tree.insert("", "end", values=(key, "" if value is None else value))

    # ------------------------------------------------------------------ 杂项

    def clear_log(self):
        self.log.configure(state="normal")
        self.log.delete("1.0", "end")
        self.log.configure(state="disabled")
        self._partial = ""

    def copy_log(self):
        text = self.log.get("1.0", "end").strip()
        if not text:
            return
        self.root.clipboard_clear()
        self.root.clipboard_append(text)
        self.status_var.set("日志已复制到剪贴板")

    def _load_config(self):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, ValueError):
            return

        self.link_var.set(data.get("link", ""))
        self.proxy_var.set(data.get("proxy", DEFAULT_PROXY))
        self.use_proxy.set(bool(data.get("use_proxy", False)))
        self.do_page.set(bool(data.get("do_page", True)))
        self.do_inspect.set(bool(data.get("do_inspect", True)))

        cookie = data.get("cookie", "")
        if cookie:
            self.cookie_var.set(cookie)
            self.remember_cookie.set(True)

        self._toggle_proxy()
        self._toggle_cookie()

    def _save_config(self):
        data = {
            "link": self.link_var.get().strip(),
            "proxy": self.proxy_var.get().strip(),
            "use_proxy": self.use_proxy.get(),
            "do_page": self.do_page.get(),
            "do_inspect": self.do_inspect.get(),
            # Cookie 是账号凭据，只有勾了「记住」才落盘
            "cookie": self.cookie_var.get().strip() if self.remember_cookie.get() else "",
        }

        try:
            with open(CONFIG_PATH, "w", encoding="utf-8") as handle:
                json.dump(data, handle, ensure_ascii=False, indent=2)
        except OSError:
            pass


def main():
    root = tk.Tk()

    if "--selftest" in sys.argv:
        App(root)
        root.after(400, root.destroy)
        root.mainloop()
        print("selftest ok")
        return 0

    App(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
