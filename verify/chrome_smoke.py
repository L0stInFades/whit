import ctypes as ct
from ctypes import wintypes as w
from pathlib import Path
import sys

user = ct.WinDLL("user32", use_last_error=True)
user.SetProcessDpiAwarenessContext.argtypes = [ct.c_void_p]
user.SetProcessDpiAwarenessContext(ct.c_void_p(-4))
user.GetDpiForWindow.argtypes = [w.HWND]
user.GetDpiForWindow.restype = ct.c_uint
user.SetWindowPos.argtypes = [w.HWND, w.HWND, ct.c_int, ct.c_int, ct.c_int, ct.c_int, ct.c_uint]
user.GetClientRect.argtypes = [w.HWND, ct.POINTER(w.RECT)]
user.ClientToScreen.argtypes = [w.HWND, ct.POINTER(w.POINT)]

import browser_smoke as browser


def point(x, y):
    return (x & 65535) | ((y & 65535) << 16)


def hit(x, y):
    value = ct.c_size_t()
    assert browser.user.SendMessageTimeoutW(browser.hwnd, 0x84, 0, point(x, y), 2, 500, ct.byref(value))
    return value.value


def check_strip(tab_count):
    def check():
        tabs = browser.win.descendants(control_type="TabItem")
        if len(tabs) != tab_count:
            return None
        tab = next((item for item in tabs if item.iface_selection_item.CurrentIsSelected), None)
        add = browser.element(auto_id="AddButton")
        minimize = browser.element(auto_id="Minimize")
        editor = browser.element("地址", "Edit")
        if not all((tab, add, minimize, editor)):
            return None
        tab, add, minimize, editor = (item.rectangle() for item in (tab, add, minimize, editor))
        y = add.mid_point().y
        drag = (add.right + minimize.left) // 2, y
        if minimize.left - add.right < 36 or tab.width() < 28:
            return None
        if any(hit(x, y) != 2 for x in (add.right + 6, drag[0], minimize.left - 6)):
            return None
        if hit(tab.mid_point().x, tab.mid_point().y) != 2:
            return None
        for area in (add, editor):
            center = area.mid_point()
            if hit(center.x, center.y) != 1:
                return None
        return drag

    return browser.until(check, f"caption and interactive regions with {tab_count} tabs")


def check_fields():
    def rect(name, role):
        return browser.until(lambda: browser.element(name, role), name).rectangle()

    address = rect("地址", "Edit")
    landing = rect("新标签搜索", "Edit")
    search = rect("搜索", "Button").mid_point()
    star = rect("添加书签", "Button").mid_point()
    assert abs(search.y - star.y) <= 1 and abs(star.y - address.mid_point().y) <= 1
    assert abs((address.left - search.x) - (star.x - address.right)) <= 1
    assert abs(landing.mid_point().x - browser.win.rectangle().mid_point().x) <= 2
    client = w.RECT()
    assert user.GetClientRect(browser.hwnd, ct.byref(client))
    lower = w.POINT(0, client.bottom)
    assert user.ClientToScreen(browser.hwnd, ct.byref(lower))
    scale = user.GetDpiForWindow(browser.hwnd) / 96
    viewport_top = address.mid_point().y + 22 * scale
    assert abs(landing.mid_point().y - (viewport_top + lower.y) / 2) <= 2


if __name__ == "__main__":
    browser.exe = Path(sys.argv[1])
    browser.proc = None
    try:
        browser.launch()
        scale = user.GetDpiForWindow(browser.hwnd) / 96
        assert user.SetWindowPos(browser.hwnd, 0, 40, 60, round(900 * scale), round(520 * scale), 0x14)
        x, y = check_strip(1)
        check_fields()
        assert browser.user.PostMessageW(browser.hwnd, 0xA3, 2, point(x, y))
        browser.until(lambda: user.IsZoomed(browser.hwnd), "double-click maximize")
        x, y = check_strip(1)
        assert browser.user.PostMessageW(browser.hwnd, 0xA3, 2, point(x, y))
        browser.until(lambda: not user.IsZoomed(browser.hwnd), "double-click restore")
        for _ in range(3):
            browser.click(auto_id="AddButton")
        check_strip(4)
        check_fields()
        assert user.SetWindowPos(browser.hwnd, 0, 40, 60, round(440 * scale), round(440 * scale), 0x14)
        check_strip(4)
        browser.close()
        browser.launch()
        check_strip(4)
        for _ in range(3):
            browser.click(auto_id="CloseButton")
        browser.click(auto_id="CloseButton")
        assert browser.proc.wait(timeout=8) == 0
        print("Caption drag zone, native maximize/restore and interactive tabs at both widths.")
    finally:
        if browser.proc and browser.proc.poll() is None:
            browser.close() if "hwnd" in vars(browser) else browser.proc.terminate()
