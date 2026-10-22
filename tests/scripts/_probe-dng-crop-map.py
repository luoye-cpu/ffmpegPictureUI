# -*- coding: utf-8 -*-
"""裁剪/有效区三侧对账：源厂商标签 × LibRaw 可见区/内嵌裁剪 × 产物 ActiveArea/DefaultCrop。

为什么要三侧：
  · 源侧有两套事实。① TIFF/EXIF 的 ImageWidth/Height 是**存储幅面**；
    ② 厂商 maker 标签给出"可用区/默认取景框"（ExifTool 有文档的：Canon CameraInfo
    CroppedImage*、Fuji RAF RawImageCropTopLeft/CroppedSize、PanasonicRaw
    CropTop/Left/Bottom/Right、Nikon 0x0045 "upper left pixel (x,y), size (w,h)"、
    Pentax ImageAreaOffset/RawImageSize）。LibRaw 把这些解析进
    `sizes.raw_inset_crops[0]`（0.20 起；Changelog 标注"vendor specified crop"）。
  · LibRaw 的解码输出（`sizes.width/height`）**不等于**内嵌裁剪：rawpy 0.27.1 实测
    `postprocess()` 用可见区，不用 `raw_inset_crops[0]`（除非显式
    `adjust_to_raw_inset_crop()`）。dngtool 目前照抄可见区 ⇒ 产物 DefaultCrop =
    可见区，厂商取景框**没有**进标准位。
  · 判据不是"猜哪个对"，而是把三侧读数摊开：谁等于谁、差几像素、哪一侧是
    文档化语义、哪一侧只是 LibRaw 的缺省。

本探针只读不判红：输出 CSV + 分歧表。dngtool 打不开的容器（CR3/RAF/MRW/X3F/RPi）
记 `convert_fail`，不静默跳过。

用法：py tests/scripts/_probe-dng-crop-map.py [--out DIR] [--no-convert]
"""
import argparse
import csv
import datetime
import json
import os
import subprocess
import sys

R = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
DNG = os.path.join(R, 'publish', 'PLAN', 'artifacts', 'dngtool.exe')
EX = os.path.join(R, 'publish', 'PLAN', 'exiftool', 'exiftool.exe')
DIRS = [os.path.join(R, 'tests', 'output', d) for d in
        ('raw_samples', 'raw_samples2', 'raw_samples3', 'raw_samples4')]
EXT = ('.arw', '.nef', '.raf', '.x3f', '.cr2', '.3fr', '.rwl', '.iiq', '.orf', '.cr3',
       '.rw2', '.pef', '.srw', '.mrw', '.sr2', '.srf', '.mef', '.fff', '.nrw', '.raw')
DERIVED = ('gps', 'sim', 'rec', 'revert', 'recovered', 'lensval', 'ori', 'oc',
           'closure_skeleton', 'test', 'tmp')

STD_TAGS = ('ImageWidth', 'ImageHeight', 'ExifImageWidth', 'ExifImageHeight',
            'ActiveArea', 'DefaultCropOrigin', 'DefaultCropSize',
            'OriginalDefaultCropSize', 'MaskedAreas', 'Make', 'Model')
VENDOR_TAGS = ('CanonImageWidth', 'CanonImageHeight',
               'CropLeftMargin', 'CropRightMargin', 'CropTopMargin', 'CropBottomMargin',
               'CroppedImageWidth', 'CroppedImageHeight', 'CroppedImageLeft',
               'CroppedImageTop',
               'RawImageFullSize', 'RawImageCropTopLeft', 'RawImageCroppedSize',
               'PanasonicImageWidth', 'PanasonicImageHeight', 'SensorAreas',
               'SonyImageWidth', 'SonyImageHeight',
               'ImageAreaOffset', 'RawImageSize', 'CropArea',
               'CropLeft', 'CropTop', 'CropBottom', 'CropRight',
               'OlympusImageWidth', 'OlympusImageHeight')


def run(argv):
    return subprocess.run(argv, capture_output=True, text=True, encoding='utf-8',
                          errors='replace')


def exiftool_json(path, tags):
    argv = [EX, '-json', '-G1', '-a', '-u', '-s'] + ['-' + t for t in tags] + [path]
    p = run(argv)
    try:
        arr = json.loads(p.stdout)
    except Exception:
        return {}
    if not arr:
        return {}
    # -G1 的键形如 "EXIF:ImageWidth"；同名标签可能跨组出现，保留首个（-G1 输出顺序即文件顺序）
    out = {}
    for k, v in arr[0].items():
        bare = k.split(':', 1)[-1]
        out.setdefault(bare, v)
        out[k] = v
    return out


def libraw_sizes(path):
    import rawpy
    raw = rawpy.imread(path)
    try:
        s = raw.sizes
        return dict(raw_w=s.raw_width, raw_h=s.raw_height,
                    vis_w=s.width, vis_h=s.height,
                    left=s.left_margin, top=s.top_margin,
                    inset_l=s.crop_left_margin, inset_t=s.crop_top_margin,
                    inset_w=s.crop_width, inset_h=s.crop_height)
    finally:
        raw.close()


def dngtool_info(path):
    p = run([DNG, '-info', path])
    try:
        return json.loads(p.stdout)
    except Exception:
        return {'error': (p.stdout + p.stderr).strip()[:200]}


def parse_rect4(tag_value, names=('x', 'y', 'w', 'h')):
    if not isinstance(tag_value, str):
        return None
    parts = tag_value.replace('x', ' ').split()
    if len(parts) != 4:
        return None
    try:
        v = [int(x) for x in parts]
    except ValueError:
        return None
    return dict(zip(names, v))


def parse_two(tag_value):
    if not isinstance(tag_value, str):
        return None
    parts = tag_value.split()
    if len(parts) != 2:
        return None
    try:
        return [int(x) for x in parts]
    except ValueError:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=None)
    ap.add_argument('--no-convert', action='store_true')
    a = ap.parse_args()

    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    outdir = a.out or os.path.join(R, 'tests', 'output', 'dng-crop-map', 'run-' + stamp)
    os.makedirs(outdir, exist_ok=True)

    files = []
    for d in DIRS:
        if not os.path.isdir(d):
            continue
        for f in sorted(os.listdir(d)):
            p = os.path.join(d, f)
            if (os.path.isfile(p) and f.lower().endswith(EXT)
                    and os.path.getsize(p) > 20000
                    and not f.lower().startswith(DERIVED)):
                files.append(p)

    rows = []
    for src in files:
        name = os.path.basename(src)
        row = {'file': os.path.relpath(src, R).replace('\\', '/'), 'name': name}
        try:
            row.update(libraw_sizes(src))
        except Exception as e:  # LibRaw 打不开（如 RPi.raw）⇒ 明确记录
            row['libraw'] = 'FAIL: %s' % e

        src_tags = exiftool_json(src, STD_TAGS + VENDOR_TAGS)
        for t in STD_TAGS + VENDOR_TAGS:
            if t in src_tags:
                row['src.' + t] = src_tags[t]

        info = dngtool_info(src)
        for k in ('width', 'height', 'raw_width', 'raw_height', 'error'):
            if k in info:
                row['dngtool.' + k] = info[k]

        # 产物
        if a.no_convert:
            row['convert'] = 'skipped'
        else:
            dst = os.path.join(outdir, os.path.splitext(name)[0] + '.dng')
            cp = run([DNG, '-e', '-lossless', '-i', src, '-O', dst])
            if cp.returncode != 0 or not os.path.exists(dst):
                row['convert'] = 'fail(rc=%d)' % cp.returncode
                row['convert.err'] = (cp.stderr or cp.stdout).strip().splitlines()[-1][:160]
            else:
                row['convert'] = 'ok'
                prod = exiftool_json(dst, STD_TAGS)
                for t in STD_TAGS:
                    if t in prod:
                        row['prod.' + t] = prod[t]
                try:
                    ps = libraw_sizes(dst)
                    row['prod.reader_vis'] = '%dx%d' % (ps['vis_w'], ps['vis_h'])
                except Exception as e:
                    row['prod.reader_vis'] = 'FAIL: %s' % e

        # ── 裁决：只比较"能指到文档语义"的两侧 ──────────────────────────
        note = []
        make = str(row.get('src.Make', '')).upper()
        # 原点是否有厂商来源：这些 make 的 LibRaw 解析器读到了偏移量
        # （canon.cpp:1028-1031 CameraInfo CroppedImageLeft/Top；fuji.cpp:1212-1218 RAF 0x110
        #  "top margin first, then left margin"；tiff.cpp:1424-1433 PanasonicRaw Crop*；
        #  pentax.cpp:333-339 ImageAreaOffset；nikon.cpp:588-591 0x0045 "upper left pixel"；
        #  olympus.cpp:412-424/611-623 0x0612-0x0615）。Sony 0xb02b 只给宽高
        # （sony.cpp:2149），cleft/ctop 留在 0 是 LibRaw 的缺省，不是厂商说"从 0 开始"。
        origin_src = 'vendor_tag'
        if make.startswith('SONY') or make.startswith('HASSELBLAD'):
            origin_src = 'default_zero(sony-0xb02b-wh-only)'
        row['inset_origin_source'] = origin_src if all(row.get(k) for k in ('inset_w', 'inset_h')) else ''
        inset_ok = all(row.get(k) for k in ('inset_w', 'inset_h'))
        if row.get('raw_w') and row.get('left') is not None:
            active = '%dx%d@%d,%d' % (row['vis_w'], row['vis_h'], row['left'], row['top'])
            row['libraw_active'] = active
        if inset_ok:
            il = row['inset_l'] - row['left']
            it = row['inset_t'] - row['top']
            row['inset_rel'] = '%dx%d@%d,%d' % (row['inset_w'], row['inset_h'], il, it)
            inside = (il >= 0 and it >= 0 and il + row['inset_w'] <= row['vis_w']
                      and it + row['inset_h'] <= row['vis_h'])
            row['inset_inside_active'] = 'yes' if inside else 'no'
        else:
            row['inset_rel'] = ''
            row['inset_inside_active'] = 'n/a'

        if row.get('convert') == 'fail' or row.get('convert', '').startswith('fail'):
            row['verdict'] = 'convert_fail'
        elif row.get('convert') == 'skipped':
            row['verdict'] = 'not_converted'
        else:
            dc = row.get('prod.DefaultCropSize')
            do = row.get('prod.DefaultCropOrigin')
            row['prod.crop_rect'] = '%s@%s' % (dc, do)
            if not inset_ok:
                row['verdict'] = 'no_vendor_crop(product=visible)'
            elif row.get('inset_inside_active') == 'no':
                row['verdict'] = 'vendor_crop_outside_active'
                note.append('vendor=%s active=%dx%d' % (row.get('inset_rel'),
                                                       row.get('vis_w', 0), row.get('vis_h', 0)))
            elif dc and '%dx%d' % (row['inset_w'], row['inset_h']) == dc.replace(' ', 'x'):
                row['verdict'] = 'mapped'
            else:
                row['verdict'] = 'vendor_crop_not_mapped'
                note.append('vendor=%s product=%s' % (row.get('inset_rel'), row['prod.crop_rect']))
        row['note'] = '; '.join(note)
        rows.append(row)

    cols = ['file', 'name', 'raw_w', 'raw_h', 'vis_w', 'vis_h', 'left', 'top',
            'inset_w', 'inset_h', 'inset_l', 'inset_t', 'inset_rel', 'inset_inside_active',
            'inset_origin_source', 'src.Make', 'src.Model',
            'src.ImageWidth', 'src.ImageHeight', 'src.ExifImageWidth', 'src.ExifImageHeight',
            'src.CanonImageWidth', 'src.CanonImageHeight',
            'src.CroppedImageWidth', 'src.CroppedImageHeight', 'src.CroppedImageLeft',
            'src.CroppedImageTop', 'src.CropLeftMargin', 'src.CropRightMargin',
            'src.CropTopMargin', 'src.CropBottomMargin',
            'src.RawImageFullSize', 'src.RawImageCropTopLeft', 'src.RawImageCroppedSize',
            'src.PanasonicImageWidth', 'src.PanasonicImageHeight',
            'src.SensorAreas', 'src.SonyImageWidth', 'src.SonyImageHeight',
            'src.ImageAreaOffset', 'src.RawImageSize', 'src.CropArea',
            'src.CropLeft', 'src.CropTop', 'src.CropBottom', 'src.CropRight',
            'src.OlympusImageWidth', 'src.OlympusImageHeight',
            'prod.ImageWidth', 'prod.ImageHeight', 'prod.ActiveArea',
            'prod.DefaultCropOrigin', 'prod.DefaultCropSize', 'prod.MaskedAreas',
            'prod.ExifImageWidth', 'prod.ExifImageHeight', 'prod.reader_vis',
            'convert', 'convert.err', 'verdict', 'note']
    with open(os.path.join(outdir, 'crop-map.csv'), 'w', newline='', encoding='utf-8-sig') as fh:
        w = csv.DictWriter(fh, fieldnames=cols, extrasaction='ignore')
        w.writeheader()
        for r in rows:
            w.writerow(r)

    print('out=%s rows=%d' % (outdir, len(rows)))
    counts = {}
    for r in rows:
        counts[r['verdict']] = counts.get(r['verdict'], 0) + 1
    for k in sorted(counts):
        print('  %-32s %d' % (k, counts[k]))
    for r in rows:
        if r['verdict'] == 'vendor_crop_not_mapped':
            print('  DIVERGE %-40s libraw_act=%s vendor=%s' %
                  (r['name'], r.get('libraw_active', '?'), r.get('inset_rel', '?')))
    return 0


if __name__ == '__main__':
    sys.exit(main())
