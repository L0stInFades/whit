from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Thread
import json
import os
import sqlite3
import subprocess
import time
import uuid
import winreg

from pywinauto import Desktop
from pywinauto.findwindows import ElementNotFoundError
from pywinauto.uia_defines import IUIA

from startup import collect, text, user, window_class


def until(check, label, timeout=12):
    stop = time.monotonic() + timeout
    while time.monotonic() < stop:
        try:
            if result := check():
                return result
        except ElementNotFoundError:
            pass
        time.sleep(.06)
    raise AssertionError(label)


marker = "whit-test-" + uuid.uuid4().hex[:8]
filename = marker + ".txt"
hits = []


class Site(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        name = self.path.rsplit("/", 1)[-1]
        hits.append((name, self.headers.get("Cookie", "")))
        body = ("<!doctype html><title>whit " + name + "</title>"
                "<a target='_blank' href='popup'>弹出标签</a>").encode()
        if name == "download":
            body = b"whit. download test"
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Content-Type", "application/octet-stream" if name == "download" else "text/html; charset=utf-8")
        if name == "download":
            self.send_header("Content-Disposition", f'attachment; filename="{filename}"')
        elif name == "a":
            self.send_header("Set-Cookie", "whit=regular; Path=/; Max-Age=3600")
        elif name == "private":
            self.send_header("Set-Cookie", "whit=private; Path=/; Max-Age=3600")
        self.end_headers()
        self.wfile.write(body)


def rows(query, *args):
    with sqlite3.connect(db.as_uri() + "?mode=ro", uri=True) as connection:
        return connection.execute(query, args).fetchall()


def element(name=None, kind=None, auto_id=None):
    found = win.descendants(control_type=kind)
    return next((item for item in found if (name is None or item.element_info.name == name)
                 and (auto_id is None or item.element_info.automation_id == auto_id)), None)


def click(name=None, kind="Button", **criteria):
    until(lambda: element(name, kind, **criteria), name or criteria).iface_invoke.Invoke()


def menu(name):
    click("菜单")
    click(name, "MenuItem")


def toggle(label):
    def match():
        return next((item for item in win.descendants(control_type="Button")
                     if item.element_info.name.startswith(label + " ")), None)
    until(match, label).iface_toggle.Toggle()


def close():
    user.PostMessageW(hwnd, 0x10, 0, 0)
    proc.wait(timeout=8)
    assert proc.returncode == 0, proc.returncode


def launch():
    global proc, hwnd, win
    proc = subprocess.Popen([str(exe)])
    hwnd = until(lambda: next((h for h in collect(proc.pid) if text(h) == "whit."), 0), "MSIX launch")
    win = Desktop(backend="uia").window(handle=hwnd)
    until(lambda: element(kind="Edit"), "address box")


def enter(url, editor="地址", submit=False):
    box = until(lambda: element(editor, "Edit"), editor)
    box.element_info.element.SetFocus()
    box.iface_value.SetValue(url)
    until(lambda: box.element_info.element.CurrentHasKeyboardFocus and box.get_value() == url,
          "editor focus and text")
    if submit:
        automation = IUIA()
        client = automation.ui_automation_client
        walker = automation.iuia.RawViewWalker
        query = walker.GetFirstChildElement(box.element_info.element)
        while query and query.CurrentAutomationId != "QueryButton":
            query = walker.GetNextSiblingElement(query)
        assert query, "native query button"
        query.GetCurrentPattern(client.UIA_InvokePatternId).QueryInterface(client.IUIAutomationInvokePattern).Invoke()
        return
    target = until(lambda: next((h for h in collect(parent=hwnd)
                   if window_class(h) == "InputSiteWindowClass"), None), "keyboard site")
    assert user.PostMessageW(target, 0x100, 13, 0x001C0001)
    assert user.PostMessageW(target, 0x101, 13, 0xC01C0001)


def title(path):
    until(lambda: element("whit " + path, "TabItem"), path + " tab")


def address(expected):
    until(lambda: (box := element("地址", "Edit") or element(kind="Edit")) and box.get_value() == expected, expected)


def browser():
    launch()
    enter(base.removeprefix("http://") + "/a", "新标签搜索")
    title("a")
    address(base + "/a")
    click("添加书签")
    until(lambda: rows("SELECT url FROM bookmarks") == [(base + "/a",)], "bookmark committed")
    enter(base + "/b")
    title("b")
    assert ("b", "whit=regular") in hits
    click("后退")
    address(base + "/a")
    click("前进")
    address(base + "/b")
    count = sum(name == "b" for name, _ in hits)
    click("刷新")
    until(lambda: sum(name == "b" for name, _ in hits) > count, "reload")
    until(lambda: element("刷新", "Button"), "reload completed")

    click(auto_id="AddButton")
    enter(base + "/second")
    title("second")
    until(lambda: element("whit b", "TabItem"), "first tab").iface_selection_item.Select()
    address(base + "/b")
    click("弹出标签", "Hyperlink")
    title("popup")
    until(lambda: rows("SELECT COUNT(*) FROM history WHERE url=?", base + "/popup") == [(1,)], "popup committed")
    element("whit popup", "TabItem").children(control_type="Button")[0].iface_invoke.Invoke()
    print("navigation, tabs, popup, bookmark", flush=True)

    menu("隐私标签")
    until(lambda: element("隐私浏览"), "private browsing indicator")
    enter(base + "/private", "新标签搜索", submit=True)
    title("private")
    assert element("隐私浏览") and not element("新标签搜索", "Edit")
    assert ("private", "") in hits
    enter(base + "/private-check")
    title("private-check")
    assert ("private-check", "whit=private") in hits
    assert not rows("SELECT id FROM history WHERE url LIKE '%/private%'")
    assert not element("添加书签", "Button").is_enabled()
    close()
    launch()
    enter(base + "/normal-again")
    title("normal-again")
    assert not element("隐私浏览")
    assert ("normal-again", "whit=regular") in hits

    menu("书签")
    click("whit a")
    address(base + "/a")
    menu("历史")
    until(lambda: element("whit normal-again", "Button"), "history library")
    click("关闭")

    enter(base + "/download")
    until(download.is_file, "download file")
    assert download.read_bytes() == b"whit. download test"
    until(lambda: element(filename), "download dialog")
    print("private isolation, persistence, downloads", flush=True)

    menu("隐私与安全")
    toggle("仅 HTTPS 导航")
    toggle("严格防跟踪")
    click("保存")
    until(lambda: rows("SELECT COUNT(*) FROM settings WHERE value=1") == [(2,)], "settings committed")
    enter(base + "/blocked")
    until(lambda: element("已阻止 HTTP 导航"), "HTTP blocked")
    assert not any(name == "blocked" for name, _ in hits)
    menu("隐私与安全")
    toggle("仅 HTTPS 导航")
    click("保存")
    menu("隐私与安全")
    click("清除浏览数据")
    click("清除")
    until(lambda: rows("SELECT COUNT(*) FROM history") == [(0,)], "history clear")
    assert rows("SELECT url FROM bookmarks") == [(base + "/a",)]
    enter(base + "/after-clear")
    title("after-clear")
    assert ("after-clear", "") in hits
    print("HTTPS policy, tracking setting, clear browsing data", flush=True)


def storage():
    assert rows("PRAGMA journal_mode") == [("wal",)]
    with sqlite3.connect(db) as connection:
        connection.executemany("INSERT INTO history(url,title,stamp) VALUES(?,?,?)",
                               ((base + f"/visit-{n}", f"visit-{n}", 1) for n in range(50030)))
    assert rows("SELECT count(*) FROM history") == [(50000,)]
    query = ("SELECT id,url,title,stamp FROM history WHERE id < ?1 "
             "AND (?2='' OR instr(lower(title),lower(?2)) OR instr(lower(url),lower(?2))) "
             "ORDER BY id DESC LIMIT 41")
    plan = rows("EXPLAIN QUERY PLAN " + query, 2**63 - 1, "")
    assert "USING INTEGER PRIMARY KEY" in plan[0][3], plan
    start = time.perf_counter()
    cursor = 2**63 - 1
    with sqlite3.connect(db.as_uri() + "?mode=ro", uri=True) as connection:
        for _ in range(100):
            page = connection.execute(query, (cursor, "")).fetchall()
            assert len(page) == 41
            cursor = page[39][0]
    print(f"50,000 visits, 100 pages in {(time.perf_counter() - start) * 1000:.1f} ms", flush=True)
    menu("历史")
    until(lambda: element("visit-50029", "Button"), "recent history")
    click("下一页")
    until(lambda: element("visit-49989", "Button"), "older history")
    click("关闭")


if __name__ == "__main__":
    package = json.loads(subprocess.check_output(
        ["pwsh", "-NoProfile", "-Command",
         "Get-AppxPackage whit.browser | Select-Object PackageFamilyName,InstallLocation | ConvertTo-Json -Compress"],
        text=True))
    exe = Path(package["InstallLocation"]) / "whit.exe"
    db = Path(os.environ["LOCALAPPDATA"]) / "Packages" / package["PackageFamilyName"] / "LocalState" / "whit.db"
    assert not db.exists(), "Install a fresh test package before running the destructive privacy test."
    assert not any(text(h) == "whit." for h in collect()), "Close whit. before testing."
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                        r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders") as key:
        value = winreg.QueryValueEx(key, "{374DE290-123F-4565-9164-39C4925E467B}")[0]
    download = Path(os.path.expandvars(value)) / filename
    server = ThreadingHTTPServer(("127.0.0.1", 0), Site)
    base = f"http://127.0.0.1:{server.server_port}/{marker}"
    Thread(target=server.serve_forever, daemon=True).start()
    proc = None
    try:
        browser()
        storage()
    finally:
        if proc and proc.poll() is None:
            close() if "hwnd" in globals() else proc.terminate()
        download.unlink(missing_ok=True)
        server.shutdown()
