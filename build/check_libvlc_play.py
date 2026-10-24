# -*- coding: utf-8 -*-
"""一次性：用裁剪后的 libvlc 直接解码一个 mp4，确认播放链路完好。

用法: python build/_check_libvlc.py <libvlc 所在目录> <视频路径>
"""
import ctypes
import os
import sys
import time

STATE = {0: "NothingSpecial", 1: "Opening", 2: "Buffering", 3: "Playing",
         4: "Paused", 5: "Stopped", 6: "Ended", 7: "Error"}


def main():
    vlc_dir = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".")
    sample = sys.argv[2] if len(sys.argv) > 2 else r"C:\测试\clip_a.mp4"
    dll = os.path.join(vlc_dir, "libvlc.dll")
    if not os.path.exists(dll):
        print("找不到", dll)
        return 1

    os.add_dll_directory(vlc_dir)
    lv = ctypes.CDLL(dll)

    lv.libvlc_new.restype = ctypes.c_void_p
    lv.libvlc_new.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_char_p)]
    lv.libvlc_media_new_path.restype = ctypes.c_void_p
    lv.libvlc_media_new_path.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    lv.libvlc_media_new_location.restype = ctypes.c_void_p
    lv.libvlc_media_new_location.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    lv.libvlc_media_player_new_from_media.restype = ctypes.c_void_p
    lv.libvlc_media_player_new_from_media.argtypes = [ctypes.c_void_p]
    lv.libvlc_media_player_play.restype = ctypes.c_int
    lv.libvlc_media_player_play.argtypes = [ctypes.c_void_p]
    lv.libvlc_media_player_get_state.restype = ctypes.c_int
    lv.libvlc_media_player_get_state.argtypes = [ctypes.c_void_p]
    lv.libvlc_media_player_get_time.restype = ctypes.c_longlong
    lv.libvlc_media_player_get_time.argtypes = [ctypes.c_void_p]
    lv.libvlc_media_player_get_length.restype = ctypes.c_longlong
    lv.libvlc_media_player_get_length.argtypes = [ctypes.c_void_p]
    lv.libvlc_media_player_stop.argtypes = [ctypes.c_void_p]
    lv.libvlc_media_player_release.argtypes = [ctypes.c_void_p]
    lv.libvlc_media_release.argtypes = [ctypes.c_void_p]
    lv.libvlc_release.argtypes = [ctypes.c_void_p]

    args = [
        b"--plugin-path=" + os.path.join(vlc_dir, "plugins").encode("utf-8"),
        b"--vout=dummy",
        b"--aout=dummy",
        b"--no-video-title-show",
        b"--quiet",
    ]
    arr = (ctypes.c_char_p * len(args))(*args)
    inst = lv.libvlc_new(len(args), arr)
    if not inst:
        print("FAIL libvlc_new 返回空（插件初始化失败）")
        return 1
    print(f"OK libvlc_new（目录 {vlc_dir}）")

    uri = "file:///" + sample.replace("\\", "/")
    media = lv.libvlc_media_new_location(inst, uri.encode("utf-8"))
    if not media:
        print("FAIL media 创建失败", uri)
        return 1
    mp = lv.libvlc_media_player_new_from_media(media)
    rc = lv.libvlc_media_player_play(mp)
    print("play rc =", rc)

    best = 0
    for _ in range(40):
        time.sleep(0.5)
        st = lv.libvlc_media_player_get_state(mp)
        t = lv.libvlc_media_player_get_time(mp)
        best = max(best, t)
        if st in (3, 6) and t > 0:
            break
        if st == 7:
            break
    st = lv.libvlc_media_player_get_state(mp)
    length = lv.libvlc_media_player_get_length(mp)
    print(f"state={STATE.get(st, st)}  time={best} ms  length={length} ms")

    lv.libvlc_media_player_stop(mp)
    lv.libvlc_media_player_release(mp)
    lv.libvlc_media_release(media)
    lv.libvlc_release(inst)

    ok = st in (3, 6, 4) and best > 0
    print("RESULT", "PLAY_OK" if ok else "PLAY_FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
