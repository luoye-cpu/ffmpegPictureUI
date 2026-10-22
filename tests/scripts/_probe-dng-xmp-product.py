#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""_probe-dng-xmp-product.py —— DNG 出口的 XMP(700) 是否真的被产品后置链搬回来（第 77 条 ㉘）

背景（账本 §40.9）：dngtool 构建 `qDNGUseXMP=0` ⇒ 它写不了 XMP；DNG 出口的 XMP 只能靠产品侧
`BuildDngStdTagsCarryArgs` 那条 exiftool 后置。原来白名单里**没有 XMP** ⇒ DNG→DNG 的评级/关键词/
crs 设置静默丢。修法是给串里加 `-XMP:all`（显式 `--strip-xmp` 时不加）。

判据（三段，全部现场量）：
  1) 用 `ServiceProbe dng-xmp-args` 取**产品自己拼出来的两条串**（default / stripxmp）——
     不在这里手抄一遍参数，避免"判据与产品各写一份"；
  2) 默认串：源件（带标准 XMP: CreatorTool / Rating / dc:subject）→ 经 dngtool 出件（XMP 已丢）
     → 按默认串跑 exiftool ⇒ 三个键必须逐字回来，且 `dngtool -verify` 仍 rc=0；
  3) 负控：同一件按 stripxmp 串跑 ⇒ 三个键必须**都不在**（两臂真的分叉）。

退出码：0 = 1)2)3) 全成立；1 = 有齿项不成立；2 = 跑不动（缺 ServiceProbe/exiftool/dngtool ⇒ 未判）。

用法：py _probe-dng-xmp-product.py <ServiceProbe.exe> <dngtool.exe> <exiftool.exe> <outdir>
"""
import importlib.util
import os
import shutil
import subprocess
import sys

_S = importlib.util.spec_from_file_location(
    'secfix', os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           '_make-secondary-ifd-fixture.py'))
sf = importlib.util.module_from_spec(_S)
_S.loader.exec_module(sf)

KEYS = ('XMP:CreatorTool', 'XMP:Rating', 'XMP:Subject')


def run(cmd, cwd=None):
    # exiftool 的 perl 会按 LC_ALL 打一堆 locale 警告到 stderr ⇒ 固定 C，避免把警告当成读数
    env = dict(os.environ, LC_ALL='C', LANG='C')
    p = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8',
                       errors='replace', cwd=cwd, env=env)
    return p.returncode, ((p.stdout or '') + (p.stderr or ''))


def run_stdout(cmd, cwd=None):
    env = dict(os.environ, LC_ALL='C', LANG='C')
    p = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8',
                       errors='replace', cwd=cwd, env=env)
    return p.returncode, (p.stdout or '')


def xmp_args(probe):
    rc, talk = run([probe, 'dng-xmp-args'])
    if rc != 0:
        # 测试宿主自己判红 = 产品的参数串不合格 ⇒ 这是**红**（rc=1），不是"跑不动"。
        # 只有它连一句 FAIL 都没打出来（崩/缺依赖）才记未判。
        status = 1 if 'FAIL ' in talk else 2
        return None, None, 'ServiceProbe rc=%d :: %s' % (rc, talk.strip()[-200:]), status
    default = strip = None
    for line in talk.splitlines():
        if line.startswith('DNG-XMP-ARGS default='):
            default = line.split('=', 1)[1]
        elif line.startswith('DNG-XMP-ARGS stripxmp='):
            strip = line.split('=', 1)[1]
    if default is None or strip is None:
        return None, None, '没拿到两条参数串 :: %s' % talk.strip()[-200:], 2
    return default, strip, '', 0


def read_keys(ex, path):
    """按三个键逐字读回；缺的记空串。"""
    out = {}
    for k in KEYS:
        _rc, talk = run_stdout([ex, '-s', '-n', '-XMP:' + k.split(':', 1)[1], path])
        first = talk.strip().splitlines()[0] if talk.strip() else ''
        out[k] = first.split(':', 1)[1].strip() if ':' in first else ''
    return out


def has_xmp(ex, path):
    _rc, talk = run_stdout([ex, '-s', '-XMP:all', path])
    return any(l.strip() for l in talk.splitlines())


def main(argv):
    if len(argv) < 5:
        print(__doc__)
        return 2
    probe, dngtool, ex, outdir = argv[1], argv[2], argv[3], argv[4]
    for p in (probe, dngtool, ex):
        if not os.path.exists(p):
            print('XMP-PROD 未判 缺件: %s' % p)
            return 2
    default, strip, why, st = xmp_args(probe)
    if default is None:
        print(('XMP-PROD FAIL ' if st == 1 else 'XMP-PROD 未判 ') + why)
        return st
    if '-XMP:all' not in default or '-XMP:all' in strip:
        print('XMP-PROD FAIL 参数串没有真分叉：default 含 -XMP:all=%s，stripxmp 含 -XMP:all=%s'
              % ('-XMP:all' in default, '-XMP:all' in strip))
        return 1

    wd = os.path.join(outdir, 'xmp-prod')
    if os.path.isdir(wd):
        shutil.rmtree(wd)
    os.makedirs(wd)
    src = os.path.join(wd, 'SRC.dng')
    sf.build(src)                                        # 源件：标准 XMP + 次级 IFD 全副
    keys0 = read_keys(ex, src)
    if keys0['XMP:CreatorTool'] != 'fixturetool' or keys0['XMP:Rating'] != '5' or not has_xmp(ex, src):
        print('XMP-PROD FAIL 源件标准 XMP 自检不过: %s' % keys0)
        return 2

    mid = os.path.join(wd, 'MID.dng')
    rc, talk = run([dngtool, '-e', '-lossless', '-i', src, '-O', mid])
    if rc != 0 or not os.path.exists(mid):
        print('XMP-PROD FAIL dngtool 没出件（rc=%d）:: %s' % (rc, talk.strip()[-160:]))
        return 2
    if has_xmp(ex, mid):
        print('XMP-PROD FAIL dngtool 直出件里居然有 XMP（前提变了：qDNGUseXMP 不再是 0？）')
        return 1

    def apply(args, name):
        dst = os.path.join(wd, name)
        shutil.copyfile(mid, dst)
        cmd = [ex] + args.replace('"', '').split()      # 工作目录里没有带空格的名字 ⇒ 直接切
        r, t = run(cmd, cwd=wd)
        return r, t, dst

    rc1, t1, dst = apply(default, 'DST.dng')
    k1 = read_keys(ex, dst)
    v_rc, v_talk = run([dngtool, '-verify', '-i', dst])
    print('XMP-PROD 读数 default 串 rc=%d 读回=%s dngtool-verify rc=%d'
          % (rc1, k1, v_rc))
    fails = []
    if rc1 != 0:
        fails.append('默认串 exiftool 失败 rc=%d :: %s' % (rc1, t1.strip()[-160:]))
    if k1['XMP:CreatorTool'] != 'fixturetool' or k1['XMP:Rating'] != '5':
        fails.append('默认串没把 XMP 抄回来（%s）' % k1)
    if v_rc != 0:
        fails.append('抄完 XMP 后 dngtool -verify 失败 rc=%d' % v_rc)

    rc2, t2, dst2 = apply(strip, 'DST_STRIP.dng')
    k2 = read_keys(ex, dst2)
    print('XMP-PROD 读数 stripxmp 串 rc=%d 读回=%s' % (rc2, k2))
    if rc2 != 0:
        fails.append('stripxmp 串 exiftool 失败 rc=%d' % rc2)
    if k2['XMP:CreatorTool'] or k2['XMP:Rating']:
        fails.append('负控不成立：--strip-xmp 串仍然抄进了 XMP（%s）' % k2)
    if fails:
        for f in fails:
            print('XMP-PROD FAIL %s' % f)
        return 1
    print('XMP-PROD OK 产品默认串把标准 XMP 抄回 DNG 且 -verify 通过；stripxmp 串不抄（两臂分叉）')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
