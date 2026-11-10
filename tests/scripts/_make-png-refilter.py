#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
_make-png-refilter.py — 统一 PNG filter 对照臂 harness（C-TOOLLIST@1 表内臂 12/13/14）。

契约锚点（.codeartsdoer/plans/png-extreme-compression/plan.md §2.2 / §3.1 T3）：
  * C-REFILTER@1  — CLI 形态、五步重打包语义、退出码 0/2/3/4/5、交叉验证判据
                    （zlib9 vs ffmpeg -compression_level 9 ≤ 2%）。
  * C-PIXEL@1     — 像素逐位等价判据口径（md5 对撞由 T1 冒烟 / T4 全量侧实现；
                    本脚本只负责产出无损输出链本身）。
  * C-TOOLLIST@1  — 臂 12/13/14 = libdeflate-12 / zlib9 / zlibng。

覆盖面：bit depth ∈ {8,16} × color type ∈ {0,2,4,6}；color type 3（pal8）、sub-byte
位深（1/2/4）、隔行（Adam7）显式拒绝（exit 2，不在 benchmark 矩阵内，防御性 fail-closed）。

五步语义（与 C-REFILTER@1 一一对应）：
  ① 签名校验 + chunk 流解析 + CRC 全校验（损坏 → exit 4）
  ② 拼接全部 IDAT → zlib 解压 → 逐行还原 raw scanline（保存每行原 filter type 字节）
  ③ 逐行统一施加 paeth filter（PNG 规范 §6.3 filter type 4）
  ④ 按 --impl 重压（见下）
  ⑤ 重写：IHDR 原样、非 IDAT chunks 按原序原样保留（iCCP/cICP/eXIf/tEXt 不得增删改）、
     新单个 IDAT 替换旧全部 IDAT、IEND 保留。

退出码：0 成功；2 输入非法/非 PNG；3 --impl 所需 dll 缺失或符号不可用；
4 CRC 或结构损坏；5 压缩实现异常。stderr 恒为单行原因摘要，不抛 traceback。

契约文本的两处实现前裁决（T1 实测校准结论若不同，改这两处常量即可）：
  * C-REFILTER@1 文本 "libdeflate_alloc_compressor(6)" 与同句 "level 12" 及
    C-TOOLLIST@1 臂表 "libdeflate-12 … level 12" 冲突 —— 从臂表取 level=12
    （alloc_compressor 的参数即 compression level）。
  * C-TOOLLIST@1 臂 14 只钉"默认策略"（strategy），未钉 level —— 取 level=9，
    使 zlibng 与 zlib9 构成同档（L9）同 filter（paeth）对撞，隔离"压缩器实现"
    这单一变量。

绑定（按 libdeflate / zlib-ng 官方头文件签名；已按 T1 交付 dll 实测校准，2026-11-09）：
  libdeflate 1.26（官方 windows-x86_64-bin 包）: libdeflate_alloc_compressor /
      libdeflate_zlib_compress / libdeflate_free_compressor 三符号实测可用（T1 探针 +
      T3 复测，zlib_compress 输出可解压往返）；libdeflate_zlib_compress_bound 符号存在
      但对任意输入返回同一垃圾常量（≈1.4e14）——判定不可用，缓冲上界恒用保守公式。
  zlib-ng 2.3.3（官方 compat zip 的 bin/zlib1.dll，ZLIB_COMPAT 构建）:
      deflateInit2_/deflate/deflateEnd/deflateBound/zlibVersion 全可用，zlibVersion()
      ='1.3.1.zlib-ng'，Win64 sizeof(z_stream)=88（uLong=32bit LLP64 布局，与 zlib.h
      同形）；version 参数取 dll 自报 zlibVersion() 返回值。z_stream 的 next_in/next_out
      字段必须显式 ctypes.cast 赋值（c_void_p 字段直接赋 bytes 在 Python 3.12 报
      TypeError，实测）。

Python 3.12 标准库，零第三方依赖。24MP 大图的 filter 循环为纯 Python
（每帧约分钟级读数），单格超时归属 T4 的 600 s 维度（C-BENCH@1）。
"""

from __future__ import annotations

import argparse
import ctypes
import os
import random
import sys
import zlib

# ---------------------------------------------------------------------------
# 退出码（C-REFILTER@1）
# ---------------------------------------------------------------------------
EXIT_OK = 0
EXIT_OTHER = 1      # 契约未定义的意外错误（如输出写失败），兜底不抛 traceback
EXIT_INPUT = 2      # 输入非法 / 非 PNG / pal8 / 不支持位深 / 隔行
EXIT_DLL = 3        # --impl 所需 dll 缺失或符号不可用
EXIT_CRC = 4        # CRC 或结构损坏
EXIT_IMPL = 5       # 压缩实现异常

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

_FILTER_PAETH = 4
_FILTER_TYPES = (0, 1, 2, 3, 4)
_CHANNELS_BY_COLOR_TYPE = {0: 1, 2: 3, 4: 2, 6: 4}

# zlib 流常量
_Z_FINISH = 4
_Z_STREAM_END = 1
_Z_DEFLATED = 8

# --impl 各臂的压缩参数（契约裁决见 head 注释；T1 实测后如需调整只改这三行）
_ZLIB9_LEVEL = 9            # zlib.compressobj(9)，默认策略
_LIBDEFLATE_LEVEL = 12      # libdeflate_alloc_compressor(level)
_ZLIBNG_LEVEL = 9           # zlib-ng deflateInit2_ level（与 zlib9 同档对撞）

# dll 候选文件名（T1 实证 2026-11-09：libdeflate 1.26 官方 windows-x86_64-bin 包落盘名
# libdeflate.dll；zlib-ng 2.3.3 官方 compat zip 落盘名 zlib1.dll —— 两实名置前，其余
# 候选保留作未来批次防御；T1 交付以 tools.json exe_path 为准）
_LIBDEFLATE_DLL_CANDIDATES = (
    "libdeflate.dll", "libdeflate_x64.dll", "liblibdeflate.dll",
)
_ZLIBNG_DLL_CANDIDATES = (
    "zlib1.dll", "zlib-ng2.dll", "zlib-ng.dll", "zlibng.dll", "libz-ng-2.dll",
    "libz-ng.dll", "zlib-ng1.dll", "zlib-ng2-2.dll",
)


class RefilterError(Exception):
    """携带退出码的单行原因异常（C-REFILTER@1 退出码表的唯一来源）。"""

    def __init__(self, code: int, reason: str):
        super().__init__(reason)
        self.code = code
        self.reason = reason


class ImplError(RefilterError):
    """压缩实现异常（exit 5）。"""

    def __init__(self, reason: str):
        super().__init__(EXIT_IMPL, reason)


def _fail(code: int, reason: str) -> None:
    raise RefilterError(code, reason)


# ---------------------------------------------------------------------------
# ① PNG chunk 流解析 + CRC 全校验
# ---------------------------------------------------------------------------
def parse_png(data: bytes) -> list[tuple[bytes, bytes, bytes]]:
    """解析 chunk 流，返回 (type, data, 原始字节) 三元组列表（保持原序）。

    签名不符 → exit 2（非 PNG）；流截断/长度越界/CRC 不符/缺 IEND → exit 4。
    """
    if not data.startswith(PNG_SIGNATURE):
        _fail(EXIT_INPUT, "input is not a PNG (signature mismatch)")
    chunks: list[tuple[bytes, bytes, bytes]] = []
    pos = len(PNG_SIGNATURE)
    total = len(data)
    while pos < total:
        if pos + 8 > total:
            _fail(EXIT_CRC, f"structural corruption: truncated chunk header at offset {pos}")
        length = int.from_bytes(data[pos:pos + 4], "big")
        ctype = data[pos + 4:pos + 8]
        if length > 0x7FFFFFFF:
            _fail(EXIT_CRC, f"structural corruption: chunk length {length} exceeds 2^31-1 at offset {pos}")
        pos += 8
        end = pos + length
        if end + 4 > total:
            _fail(EXIT_CRC, f"structural corruption: chunk {ctype.decode('latin-1', 'replace')!r} truncated")
        cdata = data[pos:end]
        crc_stored = int.from_bytes(data[end:end + 4], "big")
        crc_calc = zlib.crc32(ctype + cdata) & 0xFFFFFFFF
        if crc_stored != crc_calc:
            _fail(EXIT_CRC, f"CRC mismatch in chunk {ctype.decode('latin-1', 'replace')!r} at offset {pos - 8}")
        chunks.append((ctype, cdata, data[pos - 8:end + 4]))
        pos = end + 4
    if not chunks:
        _fail(EXIT_CRC, "structural corruption: no chunks after signature")
    if chunks[-1][0] != b"IEND":
        _fail(EXIT_CRC, "structural corruption: stream does not end with IEND")
    return chunks


def parse_ihdr(chunks: list[tuple[bytes, bytes, bytes]]) -> tuple[int, int, int, int]:
    """解析 IHDR（必须为首个 chunk、13 字节），返回 (width, height, depth, bpp)。

    color type 3（pal8）→ exit 2（显式拒绝，契约要求）；其余非法组合按语义分流。
    """
    ctype, cdata, _ = chunks[0]
    if ctype != b"IHDR" or len(cdata) != 13:
        _fail(EXIT_CRC, "structural corruption: IHDR missing / not first / not 13 bytes")
    width = int.from_bytes(cdata[0:4], "big")
    height = int.from_bytes(cdata[4:8], "big")
    depth = cdata[8]
    color_type = cdata[9]
    compression = cdata[10]
    filter_method = cdata[11]
    interlace = cdata[12]
    if compression != 0:
        _fail(EXIT_CRC, f"structural corruption: IHDR compression method {compression} != 0")
    if filter_method != 0:
        _fail(EXIT_CRC, f"structural corruption: IHDR filter method {filter_method} != 0")
    if width == 0 or height == 0:
        _fail(EXIT_CRC, "structural corruption: zero image dimension")
    if interlace != 0:
        _fail(EXIT_INPUT, "interlaced (Adam7) PNG rejected: harness supports interlace=0 only (matrix scope)")
    if color_type == 3:
        _fail(EXIT_INPUT, "palette PNG (color type 3) rejected: not in benchmark matrix")
    if color_type not in _CHANNELS_BY_COLOR_TYPE:
        _fail(EXIT_CRC, f"structural corruption: unknown color type {color_type}")
    if depth not in (8, 16):
        _fail(EXIT_INPUT, f"bit depth {depth} rejected: harness supports 8/16 only (sub-byte not in matrix)")
    channels = _CHANNELS_BY_COLOR_TYPE[color_type]
    return width, height, depth, channels * (depth // 8)


# ---------------------------------------------------------------------------
# ② unfilter：按行还原 raw scanline（PNG 规范 §6.3，5 种 filter type 全支持）
# ---------------------------------------------------------------------------
def unfilter_row(ftype: int, filt: bytes, prev: bytes, bpp: int) -> bytes:
    """单行 unfilter。prev = 上一行重建 scanline（首行传全零，b/c 视作 0）。

    ftype 越界由调用方检查（main 流程在行分派前验证 0..4）。
    """
    stride = len(filt)
    if ftype == 0:
        return filt
    if ftype == 2:  # Up：无递推（只依赖上一行），zip 向量化
        return bytes((f + p) & 0xFF for f, p in zip(filt, prev))
    cur = bytearray(stride)
    if ftype == 1:  # Sub：递推依赖左 bpp 字节
        for i in range(stride):
            a = cur[i - bpp] if i >= bpp else 0
            cur[i] = (filt[i] + a) & 0xFF
    elif ftype == 3:  # Average
        for i in range(stride):
            a = cur[i - bpp] if i >= bpp else 0
            cur[i] = (filt[i] + ((a + prev[i]) >> 1)) & 0xFF
    else:  # ftype == 4  Paeth
        for i in range(stride):
            a = cur[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            p = a + b - c
            pa = p - a
            if pa < 0:
                pa = -pa
            pb = p - b
            if pb < 0:
                pb = -pb
            pc = p - c
            if pc < 0:
                pc = -pc
            if pa <= pb and pa <= pc:
                pr = a
            elif pb <= pc:
                pr = b
            else:
                pr = c
            cur[i] = (filt[i] + pr) & 0xFF
    return bytes(cur)


# ---------------------------------------------------------------------------
# ③ 统一 paeth re-filter（PNG 规范 §6.3 filter type 4）
# ---------------------------------------------------------------------------
def _paeth_predict_stream(a_seq, b_seq, c_seq):
    """行主体 paeth 预测器流（公式同 §6.3，与 _paeth_predict_ref 互为对撞件）。

    独立成生成器是因为 PEP 572 不允许 := 出现在 comprehension 的 iterable
    表达式中；预测器流与 raw 差分分两段 zip 合成最终 filtered 行。
    """
    for a, b, c in zip(a_seq, b_seq, c_seq):
        # p = a+b-c；pa = |p-a| = |b-c|；pb = |p-b| = |a-c|；pc = |p-c| = |a+b-2c|
        p = a + b - c
        pa = p - a
        if pa < 0:
            pa = -pa
        pb = p - b
        if pb < 0:
            pb = -pb
        pc = p - c
        if pc < 0:
            pc = -pc
        yield a if pa <= pb and pa <= pc else (b if pb <= pc else c)


def refilter_paeth_row(raw: bytes, prev: bytes, bpp: int) -> bytes:
    """对单行统一施加 paeth filter：out[i] = raw[i] - PaethPredictor(a,b,c)。

    a = raw[i-bpp]（当前行左，首段取 0）、b = prev[i]（上行）、c = prev[i-bpp]（左上）。
    主体无递推（a/c 均来自已知序列）→ 按 bpp 对齐切片 zip 向量化；首段 i<bpp 时
    a=c=0，预测器恒等于 b（pa=pb=b、pc=b → pb=0 最小），用短循环处理。
    与 --selftest 内的逐字节参考实现互为对撞件。
    """
    stride = len(raw)
    out = bytearray(stride)
    head = min(bpp, stride)
    for i in range(head):
        out[i] = (raw[i] - prev[i]) & 0xFF
    if stride > head:
        out[head:] = bytes(
            (r - pr) & 0xFF
            for r, pr in zip(
                raw[head:],
                _paeth_predict_stream(raw[:-head], prev[head:], prev[:-head]),
            )
        )
    return bytes(out)


# ---------------------------------------------------------------------------
# --selftest 参考实现（逐字节直写版，与主路径双实现对撞用）
# ---------------------------------------------------------------------------
def _paeth_predict_ref(a: int, b: int, c: int) -> int:
    """PNG 规范 §6.3 PaethPredictor，直写参考版（selftest 对撞目标）。"""
    p = a + b - c
    pa = abs(p - a)
    pb = abs(p - b)
    pc = abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def refilter_paeth_row_ref(raw: bytes, prev: bytes, bpp: int) -> bytes:
    """refilter_paeth_row 的逐字节参考版（selftest 双实现对撞目标）。"""
    stride = len(raw)
    out = bytearray(stride)
    for i in range(stride):
        a = raw[i - bpp] if i >= bpp else 0
        b = prev[i]
        c = prev[i - bpp] if i >= bpp else 0
        out[i] = (raw[i] - _paeth_predict_ref(a, b, c)) & 0xFF
    return bytes(out)


def _encode_filter_row_ref(ftype: int, raw: bytes, prev: bytes, bpp: int) -> bytes:
    """PNG §6.3 filter 正向编码（直写参考版；仅 --selftest 往返对撞用）。"""
    stride = len(raw)
    out = bytearray(stride)
    for i in range(stride):
        a = raw[i - bpp] if i >= bpp else 0
        b = prev[i]
        c = prev[i - bpp] if i >= bpp else 0
        if ftype == 0:
            x = raw[i]
        elif ftype == 1:
            x = raw[i] - a
        elif ftype == 2:
            x = raw[i] - b
        elif ftype == 3:
            x = raw[i] - ((a + b) >> 1)
        else:
            x = raw[i] - _paeth_predict_ref(a, b, c)
        out[i] = x & 0xFF
    return ftype.to_bytes(1, "big") + bytes(out)


# ---------------------------------------------------------------------------
# ④ --impl 三实现（dll 绑定以 T1 交付实测校准为准）
# ---------------------------------------------------------------------------
def _load_dll(impl: str, dll_root: str | None) -> tuple[ctypes.CDLL, str]:
    """在 --dll-root 下按候选名探测并加载 dll；缺失/加载失败 → exit 3（单行原因）。"""
    candidates = _LIBDEFLATE_DLL_CANDIDATES if impl == "libdeflate12" else _ZLIBNG_DLL_CANDIDATES
    if not dll_root:
        _fail(EXIT_DLL, f"--dll-root required for --impl {impl}: dll root not given")
    if not os.path.isdir(dll_root):
        _fail(EXIT_DLL, f"--impl {impl}: dll root not found: {dll_root}")
    load_errors: list[str] = []
    for name in candidates:
        path = os.path.join(dll_root, name)
        if not os.path.isfile(path):
            continue
        try:
            return ctypes.WinDLL(path), name
        except OSError as exc:
            load_errors.append(f"{name}: {exc}")
    detail = "; ".join(load_errors) if load_errors else "no candidate file present"
    _fail(EXIT_DLL, f"--impl {impl}: dll missing under {dll_root} "
                    f"(candidates: {', '.join(candidates)}); {detail}")


def make_zlib9(dll_root: str | None) -> tuple[callable, str]:
    """zlib9 臂：Python 标准库 zlib.compressobj(9)（零外部依赖，永远可跑）。"""
    del dll_root  # zlib9 无外部依赖（契约：对照臂结果的下限锚）

    def compress_zlib(data: bytes) -> bytes:
        co = zlib.compressobj(_ZLIB9_LEVEL)  # wbits=15（RFC1950）、memLevel=8、默认策略
        return co.compress(data) + co.flush()

    return compress_zlib, "python-stdlib-zlib"


def make_libdeflate12(dll_root: str | None) -> tuple[callable, str]:
    """libdeflate12 臂：ctypes 绑定 libdeflate（**以 T1 交付 dll 实测校准为准**）。

    官方签名（libdeflate.h）：
      struct libdeflate_compressor *libdeflate_alloc_compressor(int compression_level);
      size_t libdeflate_zlib_compress(struct libdeflate_compressor *c,
          const void *in, size_t in_nbytes, void *out, size_t out_nbytes_avail);
      void libdeflate_free_compressor(struct libdeflate_compressor *c);
      size_t libdeflate_zlib_compress_bound(size_t in_nbytes);   // 可选，≥ 1.9
    """
    dll, name = _load_dll("libdeflate12", dll_root)
    try:
        alloc = dll.libdeflate_alloc_compressor
    except AttributeError:
        _fail(EXIT_DLL, f"{name}: symbol libdeflate_alloc_compressor missing")
    try:
        zcomp = dll.libdeflate_zlib_compress
    except AttributeError:
        _fail(EXIT_DLL, f"{name}: symbol libdeflate_zlib_compress missing")
    try:
        free = dll.libdeflate_free_compressor
    except AttributeError:
        _fail(EXIT_DLL, f"{name}: symbol libdeflate_free_compressor missing")
    alloc.restype = ctypes.c_void_p
    alloc.argtypes = [ctypes.c_int]
    zcomp.restype = ctypes.c_size_t
    zcomp.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_size_t,
                      ctypes.c_void_p, ctypes.c_size_t]
    free.restype = None
    free.argtypes = [ctypes.c_void_p]
    # libdeflate_zlib_compress_bound 不消费：T1 实测（2026-11-09，libdeflate 1.26 官方
    # windows-x86_64-bin 包）该符号存在但对任意输入返回同一垃圾常量（≈1.4e14，无线性度），
    # 判定不可用；输出缓冲上界恒用保守公式（见 compress_zlib 内注记）。

    def compress_zlib(data: bytes) -> bytes:
        comp = alloc(_LIBDEFLATE_LEVEL)
        if not comp:
            raise ImplError("libdeflate_alloc_compressor returned NULL")
        try:
            # 保守上界：zlib 最坏情形 = stored blocks（每 65535B 块 5 字节开销 + 头尾
            # wrapper ≈ n + 0.0001n），公式给 n*1.13 + 256，充分覆盖且避免依赖实测不可用的
            # libdeflate_zlib_compress_bound（结论见上方注记）。
            avail = len(data) + (len(data) >> 3) + (len(data) >> 6) + 256
            out_buf = ctypes.create_string_buffer(avail)
            written = zcomp(comp, data, len(data), out_buf, avail)
            if written == 0:  # libdeflate：0 仅表示输出空间不足/内部错误
                raise ImplError("libdeflate_zlib_compress wrote 0 bytes "
                                f"(input {len(data)} bytes, buffer {avail})")
            return out_buf.raw[:written]
        finally:
            free(comp)

    return compress_zlib, name


class _ZStreamWin64(ctypes.Structure):
    """z_stream 按 Win64 MSVC ABI 布局（uLong = unsigned long = 32 bit，sizeof=88）。

    与 zlib / zlib-ng（ZLIB_COMPAT 构建）的 zlib.h 在 Win x64 的布局一致；
    **绑定以 T1 交付的 dll 实测校准为准**：ABI 不匹配的表现 = deflate 返回非零
    （zlib 1.2.11+ 的 stream_size 校验）→ exit 5。
    """
    _fields_ = [
        ("next_in", ctypes.c_void_p),
        ("avail_in", ctypes.c_uint),
        ("total_in", ctypes.c_ulong),
        ("next_out", ctypes.c_void_p),
        ("avail_out", ctypes.c_uint),
        ("total_out", ctypes.c_ulong),
        ("msg", ctypes.c_char_p),
        ("state", ctypes.c_void_p),
        ("zalloc", ctypes.c_void_p),
        ("zfree", ctypes.c_void_p),
        ("opaque", ctypes.c_void_p),
        ("data_type", ctypes.c_int),
        ("adler", ctypes.c_ulong),
        ("reserved", ctypes.c_ulong),
    ]


def make_zlibng(dll_root: str | None) -> tuple[callable, str]:
    """zlibng 臂：ctypes 绑定 zlib-ng 的 zlib 兼容符号（**以 T1 交付 dll 实测校准为准**）。

    官方签名（zlib-ng ZLIB_COMPAT 构建，与 zlib.h 同形）：
      int deflateInit2_(z_streamp strm, int level, int method, int windowBits,
          int memLevel, int strategy, const char *version, int stream_size);
      int deflate(z_streamp strm, int flush);
      int deflateEnd(z_streamp strm);
      uLong deflateBound(z_streamp strm, uLong sourceLen);   // 可选
      const char *zlibVersion(void);                          // version 参数取此值
    """
    dll, name = _load_dll("zlibng", dll_root)
    try:
        init2 = dll.deflateInit2_
    except AttributeError:
        _fail(EXIT_DLL, f"{name}: symbol deflateInit2_ missing (need ZLIB_COMPAT build)")
    try:
        deflate = dll.deflate
    except AttributeError:
        _fail(EXIT_DLL, f"{name}: symbol deflate missing")
    try:
        end = dll.deflateEnd
    except AttributeError:
        _fail(EXIT_DLL, f"{name}: symbol deflateEnd missing")
    init2.restype = ctypes.c_int
    init2.argtypes = [ctypes.POINTER(_ZStreamWin64), ctypes.c_int, ctypes.c_int,
                      ctypes.c_int, ctypes.c_int, ctypes.c_int,
                      ctypes.c_char_p, ctypes.c_int]
    deflate.restype = ctypes.c_int
    deflate.argtypes = [ctypes.POINTER(_ZStreamWin64), ctypes.c_int]
    end.restype = ctypes.c_int
    end.argtypes = [ctypes.POINTER(_ZStreamWin64)]
    version = None
    try:  # deflateInit2_ 校验 version 与 dll 内部 ZLIB_VERSION 一致 → 必须用 dll 自报值
        zv = dll.zlibVersion
        zv.restype = ctypes.c_char_p
        zv.argtypes = []
        version = zv()
    except AttributeError:
        pass
    bound_fn = None
    try:
        bound_fn = dll.deflateBound
        bound_fn.restype = ctypes.c_ulong
        bound_fn.argtypes = [ctypes.POINTER(_ZStreamWin64), ctypes.c_ulong]
    except AttributeError:
        pass

    def compress_zlib(data: bytes) -> bytes:
        strm = _ZStreamWin64()
        ret = init2(ctypes.byref(strm), _ZLIBNG_LEVEL, _Z_DEFLATED, 15, 8, 0,
                    version if version else b"zlib-ng",
                    ctypes.sizeof(_ZStreamWin64))
        if ret != 0:
            raise ImplError(f"deflateInit2_ returned {ret} "
                            f"(level={_ZLIBNG_LEVEL}; ABI mismatch suspected)")
        ret_end = None
        try:
            if bound_fn is not None:
                avail = bound_fn(ctypes.byref(strm), len(data)) + 64
            else:
                avail = len(data) + (len(data) >> 3) + (len(data) >> 6) + 256
            out_buf = ctypes.create_string_buffer(avail)
            # c_void_p 字段不接受 bytes 直接赋值（Python 3.12 ctypes 实测 TypeError:
            # cannot be converted to pointer）→ 显式 cast；中间指针实例存局部变量，
            # 保证所引缓冲存活至 deflate 调用结束。
            in_ptr = ctypes.c_char_p(data)
            out_ptr = ctypes.cast(out_buf, ctypes.c_void_p)
            strm.next_in = ctypes.cast(in_ptr, ctypes.c_void_p)
            strm.avail_in = len(data)
            strm.next_out = out_ptr
            strm.avail_out = avail
            ret = deflate(ctypes.byref(strm), _Z_FINISH)
            if ret != _Z_STREAM_END:
                raise ImplError(f"deflate(Z_FINISH) returned {ret}: "
                                f"{strm.msg.decode('latin-1', 'replace') if strm.msg else 'no msg'}")
            if strm.avail_in != 0:
                raise ImplError("deflate did not consume all input")
            out = out_buf.raw[: avail - strm.avail_out]
            ret_end = end(ctypes.byref(strm))
            if ret_end != 0:
                raise ImplError(f"deflateEnd returned {ret_end}")
            return out
        finally:
            if ret_end is None:  # 异常路径防御释放状态（返回值忽略）
                end(ctypes.byref(strm))

    return compress_zlib, name


_IMPL_FACTORIES = {
    "zlib9": make_zlib9,
    "libdeflate12": make_libdeflate12,
    "zlibng": make_zlibng,
}


# ---------------------------------------------------------------------------
# 主流程（五步语义）+ ⑤ 重写
# ---------------------------------------------------------------------------
def _pack_chunk(ctype: bytes, cdata: bytes) -> bytes:
    crc = zlib.crc32(ctype + cdata) & 0xFFFFFFFF
    return len(cdata).to_bytes(4, "big") + ctype + cdata + crc.to_bytes(4, "big")


def refilter(in_path: str, out_path: str, impl: str, dll_root: str | None,
             force_impl_error: bool) -> None:
    # 输入文件先行（exit 2 语义：文件不存在属"输入非法"）
    if not os.path.isfile(in_path):
        _fail(EXIT_INPUT, f"input not found: {in_path}")
    with open(in_path, "rb") as fh:
        data = fh.read()

    # ① 签名 + chunk 流 + CRC 全校验
    chunks = parse_png(data)
    del data  # chunk 已切片持有，原始缓冲即刻释放（24MP 内存峰值控制）
    width, height, depth, bpp = parse_ihdr(chunks)

    # ② 拼接全部 IDAT → zlib 解压
    idat_chunks = [c for c in chunks if c[0] == b"IDAT"]
    if not idat_chunks:
        _fail(EXIT_CRC, "structural corruption: no IDAT chunk in stream")
    joined = b"".join(c[1] for c in idat_chunks)
    try:
        dec = zlib.decompressobj()
        raw_all = dec.decompress(joined)
        if not dec.eof or dec.unused_data:
            raise zlib.error("incomplete zlib stream or trailing garbage")
    except zlib.error as exc:
        _fail(EXIT_CRC, f"zlib stream in IDAT corrupt: {exc}")
    stride = width * bpp
    expected = height * (1 + stride)
    if len(raw_all) != expected:
        _fail(EXIT_CRC, f"decompressed scanline stream {len(raw_all)} bytes "
                        f"!= IHDR-derived {expected} bytes")

    # ②+③ 逐行 unfilter（保存每行原 filter type 字节）→ 统一 paeth re-filter
    refilt = bytearray(expected)
    prev = b"\x00" * stride
    pos = 0
    for _y in range(height):
        ftype = raw_all[pos]
        if ftype not in _FILTER_TYPES:
            _fail(EXIT_CRC, f"structural corruption: scanline filter type {ftype} (must be 0..4)")
        cur = unfilter_row(ftype, raw_all[pos + 1:pos + 1 + stride], prev, bpp)
        refilt[pos] = _FILTER_PAETH
        refilt[pos + 1:pos + 1 + stride] = refilter_paeth_row(cur, prev, bpp)
        prev = cur
        pos += 1 + stride

    # ④ 按 --impl 重压（dll 加载在输入校验之后，负控顺序稳定）
    compress_zlib, dll_used = _IMPL_FACTORIES[impl](dll_root)
    if force_impl_error:  # 负控：模拟内部失败路径（exit 5），可复现
        raise ImplError("selftest: forced compression implementation error")
    zidat = compress_zlib(bytes(refilt))

    # ⑤ 重写：IHDR/非 IDAT chunks 原序原样保留（整 chunk 字节直拷，含原 CRC）、
    #    首个 IDAT 位置替换为单个新 IDAT（多 IDAT 合一合法）、IEND 保留
    out = bytearray()
    out += PNG_SIGNATURE
    replaced = False
    for ctype, _cdata, raw_bytes in chunks:
        if ctype == b"IDAT":
            if not replaced:
                out += _pack_chunk(b"IDAT", zidat)
                replaced = True
            continue
        out += raw_bytes
    if not replaced:  # 理论不可达（idat_chunks 非空已查）
        _fail(EXIT_CRC, "internal: no IDAT position found for replacement")

    with open(out_path, "wb") as fh:
        fh.write(out)


    old_idat_bytes = sum(len(c[1]) for c in idat_chunks)
    total_in = len(PNG_SIGNATURE) + sum(len(rb) for _, _, rb in chunks)
    delta_pct = (len(out) - total_in) / total_in * 100.0 if total_in else 0.0
    note = f" dll={dll_used}" if dll_used else ""
    print(f"png-refilter: ok in={total_in} out={len(out)} impl={impl} filter=paeth "
          f"idat={len(idat_chunks)}->1 idat_bytes={old_idat_bytes}->{len(zidat)} "
          f"delta={delta_pct:+.2f}%{note}")


# ---------------------------------------------------------------------------
# --selftest：paeth 向量 + 全链往返双实现对撞 + zlib9 往返（固定 seed 可复现）
# ---------------------------------------------------------------------------
def run_selftest() -> None:
    rng = random.Random(0x5EEDBEEF)

    # 1) PaethPredictor 手算向量（PNG 规范 §6.3）
    #    例：(10,20,15)：p=15, pa=5, pb=5, pc=0 → c=15；(5,3,9)：p=-1 → pb 最小 → b=3
    vectors = [
        (10, 20, 15, 15),
        (10, 20, 0, 20),
        (50, 50, 50, 50),
        (200, 100, 150, 150),
        (5, 3, 9, 3),
        (100, 100, 200, 100),
        (0, 0, 0, 0),
    ]
    for a, b, c, want in vectors:
        got = _paeth_predict_ref(a, b, c)
        assert got == want, f"paeth vector ({a},{b},{c}): got {got}, want {want}"

    # 2) 全链往返：随机 raw → 每行随机 filter type 0..4 正向编码 → 主路径 unfilter
    #    → 对撞 raw；再统一 paeth refilter（向量化主路径 vs 参考实现 vs 自解码往返）
    combos = [(8, 0), (8, 2), (8, 4), (8, 6), (16, 0), (16, 2), (16, 4), (16, 6)]
    n_round = 0
    n_bytes = 0
    for depth, color_type in combos:
        bpp = _CHANNELS_BY_COLOR_TYPE[color_type] * (depth // 8)
        for _case in range(8):
            w = rng.randint(1, 41)
            h = rng.randint(1, 9)
            stride = w * bpp
            raw_rows = [bytes(rng.randrange(256) for _ in range(stride)) for _ in range(h)]
            filt_rows = []
            prev = b"\x00" * stride
            for y in range(h):
                ftype = rng.choice(_FILTER_TYPES)
                filt_rows.append(_encode_filter_row_ref(ftype, raw_rows[y], prev, bpp))
                prev = raw_rows[y]
            prev = b"\x00" * stride
            for y in range(h):
                ftype = filt_rows[y][0]
                cur = unfilter_row(ftype, filt_rows[y][1:], prev, bpp)
                assert cur == raw_rows[y], \
                    f"unfilter mismatch depth={depth} ct={color_type} row={y}"
                enc = refilter_paeth_row(cur, prev, bpp)
                enc_ref = refilter_paeth_row_ref(cur, prev, bpp)
                assert enc == enc_ref, \
                    f"paeth refilter dual-impl mismatch depth={depth} ct={color_type} row={y}"
                back = unfilter_row(_FILTER_PAETH, enc, prev, bpp)
                assert back == cur, \
                    f"paeth refilter roundtrip mismatch depth={depth} ct={color_type} row={y}"
                prev = cur
                n_round += 1
                n_bytes += stride

    # 3) zlib9 压缩→解压往返
    blob = bytes(rng.randrange(256) for _ in range(65536))
    co = zlib.compressobj(_ZLIB9_LEVEL)
    z = co.compress(blob) + co.flush()
    assert zlib.decompress(z) == blob, "zlib9 compress/decompress roundtrip mismatch"

    print(f"png-refilter: selftest ok paeth_vectors={len(vectors)} "
          f"refilter_roundtrips={n_round} combos={len(combos)} bytes={n_bytes} "
          f"zlib9_roundtrip={len(z)}B")


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        prog="png-refilter",
        description="Unified PNG filter cross-check harness (C-REFILTER@1; "
                    "arms 12/13/14 of C-TOOLLIST@1). Pixel criteria: C-PIXEL@1.",
        allow_abbrev=False,
    )
    ap.add_argument("--in", dest="in_path", required=False, default=None, metavar="<in.png>",
                    help="input PNG (8/16-bit, color type 0/2/4/6)")
    ap.add_argument("--out", dest="out_path", required=False, default=None, metavar="<out.png>",
                    help="output PNG (unified paeth filter, single IDAT)")
    ap.add_argument("--impl", required=False, default=None, choices=tuple(_IMPL_FACTORIES),
                    help="deflate implementation: zlib9 | libdeflate12 | zlibng")
    ap.add_argument("--dll-root", dest="dll_root", default=None, metavar="<dir>",
                    help="directory holding libdeflate/zlib-ng dll (T1 deliverable)")
    ap.add_argument("--filter", default="paeth", choices=("paeth",),
                    help="unified filter type (paeth only, per C-REFILTER@1)")
    ap.add_argument("--selftest", action="store_true", help=argparse.SUPPRESS)
    ap.add_argument("--selftest-force-impl-error", dest="selftest_force_impl_error",
                    action="store_true", help=argparse.SUPPRESS)
    args = ap.parse_args(argv)
    try:
        if args.selftest:
            run_selftest()
        else:
            missing = [flag for flag, val in
                       (("--in", args.in_path), ("--out", args.out_path), ("--impl", args.impl))
                       if val is None]
            if missing:
                ap.error(f"the following arguments are required: {', '.join(missing)} "
                         f"(or use --selftest)")
            refilter(args.in_path, args.out_path, args.impl, args.dll_root,
                     args.selftest_force_impl_error)
        return EXIT_OK
    except RefilterError as exc:
        print(f"png-refilter: {exc.reason}", file=sys.stderr)
        return exc.code
    except OSError as exc:
        print(f"png-refilter: filesystem error: {exc}", file=sys.stderr)
        return EXIT_OTHER
    except Exception as exc:  # 兜底：任何意外都不抛 traceback（stderr 单行，C-REFILTER@1）
        print(f"png-refilter: internal error: {type(exc).__name__}: {exc}", file=sys.stderr)
        return EXIT_OTHER


if __name__ == "__main__":
    sys.exit(main())