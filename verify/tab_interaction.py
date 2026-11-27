from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
from pathlib import Path
from threading import Thread
import sys
import time

import win32api
import win32con
import win32gui
import win32ui
from PIL import Image

from chrome_smoke import user, browser


class Site(BaseHTTPRequestHandler):
    visits = 0
    independent_visits = 0
    icon = BytesIO()
    Image.new("RGB", (32, 32), (238, 19, 234)).save(icon, "PNG")

    def do_GET(self):
        if self.path == "/icon.png":
            data, kind = self.icon.getvalue(), "image/png"
        elif self.path == "/independent":
            Site.independent_visits += 1
            data = b"""<title>Independent</title><script>let n = 0;</script>
                <button onclick="this.textContent='Step '+(++n)">Step 0</button><input aria-label="Page field">"""
            kind = "text/html"
        else:
            if self.path == "/live":
                Site.visits += 1
            data = b"""<title>Live page</title><link rel=icon href=/icon.png>
                <button onclick="this.textContent='Preserved';document.title='Edited page'">Edit</button>
                <input aria-label="Page field"><button onclick="window.open('/popup')">Popup</button>"""
            kind = "text/html"
        self.send_response(200)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *_):
        pass


def item(name, role):
    return browser.until(lambda: browser.element(name, role), name)


def tab_icon_visible(title):
    rect = item(title, "TabItem").rectangle()
    left, top, right, bottom = win32gui.GetWindowRect(browser.hwnd)
    size = right - left, bottom - top
    dc = win32gui.GetWindowDC(browser.hwnd)
    source = win32ui.CreateDCFromHandle(dc)
    target = source.CreateCompatibleDC()
    bitmap = win32ui.CreateBitmap()
    bitmap.CreateCompatibleBitmap(source, *size)
    previous = target.SelectObject(bitmap)
    try:
        assert user.PrintWindow(browser.hwnd, target.GetSafeHdc(), 2)
        image = Image.frombytes("RGB", size, bitmap.GetBitmapBits(True), "raw", "BGRX")
        icon = image.crop((rect.left-left, rect.top-top, min(rect.left+55, rect.right)-left, rect.bottom-top))
        return sum(n for n, (r, g, b) in icon.getcolors(icon.width * icon.height)
                   if r > 210 and g < 55 and b > 210) >= 40
    finally:
        target.SelectObject(previous)
        target.DeleteDC()
        win32gui.DeleteObject(bitmap.GetHandle())
        win32gui.ReleaseDC(browser.hwnd, dc)


def place(hwnd, x, y, width, height):
    assert user.SetWindowPos(hwnd, 0, x, y, width, height, 0x40)
    win32gui.ShowWindow(hwnd, win32con.SW_SHOW)


def move(xy):
    x, y = xy
    screen_x, screen_y, width, height = (user.GetSystemMetrics(i) for i in (76, 77, 78, 79))
    win32api.mouse_event(0xC001, round((x-screen_x)*65535/(width-1)), round((y-screen_y)*65535/(height-1)), 0, 0)


def pointer(hwnd, xy):
    move(xy)
    browser.until(lambda: all(abs(a-b) <= 1 for a, b in zip(win32api.GetCursorPos(), xy)), "pointer position", 3)
    browser.until(lambda: win32gui.GetAncestor(win32gui.WindowFromPoint(xy), 2) == hwnd, "pointer over test window", 3)


def hover_and_focus_address():
    left, top, _, bottom = win32gui.GetWindowRect(browser.hwnd)
    pointer(browser.hwnd, (left + 35, bottom - 65))
    win32api.mouse_event(win32con.MOUSEEVENTF_LEFTDOWN, 0, 0)
    win32api.mouse_event(win32con.MOUSEEVENTF_LEFTUP, 0, 0)
    time.sleep(1.4)
    assert not browser.element("Ctrl+L", "ToolTip")
    win32api.keybd_event(win32con.VK_CONTROL, 0, 0)
    try:
        win32api.keybd_event(ord("L"), 0, 0)
        win32api.keybd_event(ord("L"), 0, win32con.KEYEVENTF_KEYUP)
    finally:
        win32api.keybd_event(win32con.VK_CONTROL, 0, win32con.KEYEVENTF_KEYUP)
    browser.until(lambda: item("地址", "Edit").element_info.element.CurrentHasKeyboardFocus, "Ctrl+L")


def drag_out(title):
    tab = item(title, "TabItem").rectangle()
    x, y = tab.mid_point().x, tab.mid_point().y
    first = browser.hwnd
    pointer(first, (x, y))
    win32api.mouse_event(win32con.MOUSEEVENTF_LEFTDOWN, 0, 0)
    try:
        for dy in (3, 10, 22, 40, 68, 95, 135, 176):
            move((x + dy // 3, y + dy))
            time.sleep(.025)
        def detached():
            return next((h for h in browser.windows() if h != first and win32gui.GetWindowRect(h)[1] > tab.top+40), None)
        second = browser.until(detached, "live detached window before mouse-up", 3)
        browser.attach(second)
        item("Preserved", "Button")
        assert Site.visits == 1
        browser.attach(first)
        dest = item("新标签", "TabItem").rectangle().mid_point()
        start_x, start_y = win32api.GetCursorPos()
        for step in range(1, 13):
            move((start_x+(dest.x-start_x)*step//12, start_y+(dest.y-start_y)*step//12))
            time.sleep(.025)
        item("Edited page", "TabItem")
        for dy in range(5, 176, 10):
            move((dest.x, dest.y + dy))
            time.sleep(.025)
        browser.until(lambda: win32gui.IsWindowVisible(second), "tear out again in the same gesture")
        browser.attach(second)
        item("Preserved", "Button")
        return first, second
    finally:
        win32api.mouse_event(win32con.MOUSEEVENTF_LEFTUP, 0, 0)


def merge(first, second, title="Edited page", button="Preserved"):
    place(first, 120, 90, 850, 590)
    place(second, 420, 395, 850, 590)
    source = item(title, "TabItem").rectangle().mid_point()
    browser.attach(first)
    dest = item("新标签", "TabItem").rectangle().mid_point()
    pointer(second, (source.x, source.y))
    win32api.mouse_event(win32con.MOUSEEVENTF_LEFTDOWN, 0, 0)
    try:
        for step in range(1, 21):
            move((source.x+(dest.x-source.x)*step//20, source.y+(dest.y-source.y)*step//20))
            time.sleep(.02)
        item(title, "TabItem")
    finally:
        win32api.mouse_event(win32con.MOUSEEVENTF_LEFTUP, 0, 0)
    browser.until(lambda: browser.windows() == [first], "empty window closed after merge")
    item(button, "Button")
    assert Site.visits == 1


def zoom_page():
    field = item("Page field", "Edit")
    point = field.rectangle().mid_point()
    pointer(browser.hwnd, (point.x, point.y))
    win32api.mouse_event(win32con.MOUSEEVENTF_LEFTDOWN, 0, 0)
    win32api.mouse_event(win32con.MOUSEEVENTF_LEFTUP, 0, 0)
    item("页面缩放 100%", "Text")
    win32api.keybd_event(win32con.VK_CONTROL, 0, 0)
    try:
        win32api.mouse_event(win32con.MOUSEEVENTF_WHEEL, 0, 0, 120)
        browser.until(lambda: next((el for el in browser.win.descendants(control_type="Text")
            if el.element_info.name.startswith("页面缩放 ") and el.element_info.name != "页面缩放 100%"), None), "wheel zoom")
        win32api.keybd_event(ord("0"), 0, 0)
        win32api.keybd_event(ord("0"), 0, win32con.KEYEVENTF_KEYUP)
    finally:
        win32api.keybd_event(win32con.VK_CONTROL, 0, win32con.KEYEVENTF_KEYUP)
    item("页面缩放 100%", "Text")


if __name__ == "__main__":
    browser.exe = Path(sys.argv[1])
    browser.proc = None
    server = ThreadingHTTPServer(("127.0.0.1", 0), Site)
    base = f"http://127.0.0.1:{server.server_port}"
    Thread(target=server.serve_forever, daemon=True).start()
    cursor = win32api.GetCursorPos()
    try:
        browser.launch(base + "/live")
        place(browser.hwnd, 120, 90, 930, 610)
        browser.click("Edit")
        browser.until(lambda: tab_icon_visible("Edited page"), "favicon in tab header")
        print("Live page favicon rendered in the tab header.", flush=True)
        browser.click(auto_id="AddButton")
        hover_and_focus_address()
        print("Page hover stays clear and Ctrl+L focuses the address.", flush=True)
        item("Edited page", "TabItem").iface_selection_item.Select()
        first, second = drag_out("Edited page")
        print("Detached window and live DOM visible while holding the mouse.", flush=True)
        merge(first, second)
        print("Merged with the original document and request count.", flush=True)
        zoom_page()
        print("Page zoom percentage follows Ctrl+wheel and Ctrl+0.", flush=True)
        item("新标签", "TabItem").iface_selection_item.Select()
        browser.close()
        browser.launch()
        browser.until(lambda: tab_icon_visible("Edited page"), "favicon in restored background tab")
        assert Site.visits == 1
        print("Background favicon restored without a request.", flush=True)
        first = browser.hwnd
        browser.menu("新窗口")
        original = browser.until(lambda: next((h for h in browser.windows() if h != first), None), "independent window")
        browser.attach(original)
        browser.enter(base + "/independent")
        browser.click("Step 0")
        merge(first, original, "Independent", "Step 1")
        browser.click("Step 1")
        item("Step 2", "Button")
        zoom_page()
        assert Site.independent_visits == 1
        item("Independent", "TabItem").children(control_type="Button")[0].iface_invoke.Invoke()
        browser.until(lambda: not win32gui.IsWindow(original), "native host released with its last WebView")
        print("Independent window merged; page remained interactive and its native host was released.", flush=True)
    finally:
        move(cursor)
        if browser.proc and browser.proc.poll() is None:
            for handle in browser.windows():
                browser.user.PostMessageW(handle, 0x10, 0, 0)
            browser.proc.wait(timeout=8)
        server.shutdown()
