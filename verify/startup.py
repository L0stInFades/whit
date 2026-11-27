import ctypes as ct
from ctypes import wintypes as wt
from pathlib import Path
import os
import statistics
import subprocess
import sys
import time
import uuid

user = ct.WinDLL('user32', use_last_error=True)
callback = ct.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user.EnumWindows.argtypes = [callback, wt.LPARAM]
user.EnumChildWindows.argtypes = [wt.HWND, callback, wt.LPARAM]
user.GetWindowThreadProcessId.argtypes = [wt.HWND, ct.POINTER(wt.DWORD)]
user.IsWindowVisible.argtypes = [wt.HWND]
user.IsWindowEnabled.argtypes = [wt.HWND]
user.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ct.c_int]
user.SendMessageTimeoutW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM, wt.UINT, wt.UINT, ct.POINTER(ct.c_size_t)]
user.SendMessageTimeoutW.restype = wt.LPARAM
user.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]


def collect(pid=None, parent=None):
    windows = []

    @callback
    def visit(hwnd, param):
        if pid is None:
            windows.append(hwnd)
        else:
            found = wt.DWORD()
            user.GetWindowThreadProcessId(hwnd, ct.byref(found))
            if found.value == pid and user.IsWindowVisible(hwnd):
                windows.append(hwnd)
        return True

    if parent is None:
        user.EnumWindows(visit, 0)
    else:
        user.EnumChildWindows(parent, visit, 0)
    return windows


def window_class(hwnd):
    buf = ct.create_unicode_buffer(256)
    user.GetClassNameW(hwnd, buf, len(buf))
    return buf.value


def text(hwnd):
    buf = ct.create_unicode_buffer(4096)
    user.SendMessageTimeoutW(hwnd, 0xD, len(buf), ct.addressof(buf), 2, 30, None)
    return buf.value


def launch(path, profile):
    env = os.environ | {'WEBVIEW2_USER_DATA_FOLDER': str(profile)}
    start = time.perf_counter()
    proc = subprocess.Popen([path], env=env)
    window = None
    try:
        while time.perf_counter() - start < 12:
            if proc.poll() is not None:
                raise RuntimeError(f'{path} exited {proc.returncode}')
            if not window:
                for hwnd in collect(proc.pid):
                    if text(hwnd) == 'TinyWeb':
                        window = hwnd
                        visible = (time.perf_counter() - start) * 1000
                        break
            if window:
                children = [(hwnd, window_class(hwnd)) for hwnd in collect(parent=window)]
                if (any(kind == 'Chrome_RenderWidgetHostHWND' for _, kind in children)
                        and any('EDIT' in kind.upper() and user.IsWindowEnabled(hwnd) for hwnd, kind in children)):
                    return visible, (time.perf_counter() - start) * 1000
            time.sleep(0.004)
        raise TimeoutError(f'{path}: {[(window_class(h), text(h)) for h in collect(parent=window)] if window else "no window"}')
    finally:
        if window:
            user.PostMessageW(window, 0x10, 0, 0)
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.terminate()
            proc.wait(timeout=3)


if __name__ == '__main__':
    count = int(sys.argv[1])
    paths = [Path(p).resolve() for p in sys.argv[2:]]
    base = Path(__file__).resolve().parents[1] / '.deps' / 'bench-data' / uuid.uuid4().hex[:8]
    base.mkdir(parents=True, exist_ok=True)
    for fresh in (True, False):
        values = {path: [] for path in paths}
        if not fresh:
            for path in paths:
                launch(path, base / (path.stem + '-warm'))
                time.sleep(0.5)
        for sample in range(count):
            for path in (paths if sample % 2 == 0 else list(reversed(paths))):
                profile = base / (path.stem + (f'-{sample}' if fresh else '-warm'))
                values[path].append(launch(path, profile))
                time.sleep(0.5)
        for path, result in values.items():
            print(path.name, 'new' if fresh else 'reused', 'visible/blank median ms', *(round(statistics.median(x), 1) for x in zip(*result)), 'samples', [[round(t, 1) for t in pair] for pair in result], flush=True)
