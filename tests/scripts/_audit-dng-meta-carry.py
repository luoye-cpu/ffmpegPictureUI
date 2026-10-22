# -*- coding: utf-8 -*-
"""逐实例的"私有 RAW 标签 → DNG"去留账本（第 77 条审计轮，账本 §44）。

口径（必须先读）：
  * 统计母体 = 44 件厂商样本里 exiftool `-a -u -G1` 的**厂商组标签实例**；
    标准组（IFD0/ExifIFD/GPS/XMP/...）**不在母体里**（它们由产品侧标准 EXIF 后置搬运，另有 ⑳ 组判据）。
  * 单位 = 标签实例（同一标签在 N 个文件里出现就算 N 条）。不是"信息条目"，也不是"标签定义"。
  * 每条实例给 6 个独立字段：语义类 / 标准载体 / 映射状态 / 读者解析 / 图像施加 / 处置。
  * 处置只有 8 种（互斥）：carry_verified | carry_indirect | carry_gap | info_duplicate_standard |
    drop_no_carrier | drop_semantics_unknown | policy_discarded | unaudited。
    `carry_gap` = 标准位存在、值也确实在源件里，但产品当前**没搬**（可修缺口，不是"没有载体"）。
  * 证据等级：verified（本机测试日志/产物回读）| source_inferred（源码/规范推断）| pending（未验证）。
  * 规则表没覆盖到的名字一律 `unaudited`，**不**因"名字不可分类"就记成丢失。

用法：
  py tests/scripts/_audit-dng-meta-carry.py [--out DIR] [--verify DIR]
  --verify 从 ledger.csv 重算 summary.json 并逐项对撞（不跑 exiftool）。
"""
import argparse
import collections
import csv
import hashlib
import json
import os
import re
import subprocess
import sys

R = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EX = os.path.join(R, 'publish', 'PLAN', 'exiftool', 'exiftool.exe')
DIRS = [os.path.join(R, 'tests', 'output', d) for d in
        ('raw_samples', 'raw_samples2', 'raw_samples3', 'raw_samples4')]
EXT = ('.arw', '.nef', '.raf', '.x3f', '.cr2', '.3fr', '.rwl', '.iiq', '.orf', '.cr3',
       '.rw2', '.pef', '.srw', '.mrw', '.sr2', '.srf', '.mef', '.fff', '.nrw', '.raw')
DERIVED = re.compile(r'^(gps|sim|rec|revert|recovered|lensval|ori|oc|closure_skeleton|中文名|test|tmp)', re.I)
STD = ('IFD0', 'IFD1', 'IFD2', 'IFD3', 'SubIFD', 'ExifIFD', 'GPS', 'InteropIFD', 'File', 'System',
       'Composite', 'XMP', 'Iptc', 'Photoshop', 'QuickTime', 'ICC', 'ExifTool', 'PrintIM',
       'PreviewIFD', 'Track', 'Item', 'Keys', 'MPImage', 'Huffman', 'MakerNotes')

DISPOSITIONS = ('carry_verified', 'carry_indirect', 'carry_gap', 'info_duplicate_standard',
                'drop_no_carrier', 'drop_semantics_unknown', 'policy_discarded', 'unaudited')
LEVELS = ('verified', 'source_inferred', 'pending')


def rule(name, make):
    """返回 (语义类, 载体, 映射, 读者, 施加, 处置, 理由, 证据, 等级, 规则号)。顺序即有先手。"""
    n = name
    mk = (make or '').upper()

    # ── 镜头三表：只有 Sony/Fuji/Panasonic(Leica 走容器签名) 有独立实现可对撞 ──
    if re.search(r'^(DistortionCorrParams|VignettingCorrParams|ChromaticAberrationCorrParams|'
                 r'GeometricDistortionParams|DistortionParam\d+|DistortionScale|DistortionN|'
                 r'LensDistortionParams|ChromaticAberrationParams|VignettingParams)$', n, re.I):
        if any(k in mk for k in ('SONY', 'HASSELBLAD', 'FUJI', 'PANASONIC', 'LEICA')):
            return ('镜头校正表', 'OpcodeList3(51022)', 'fitted', 'ACR;dt(1/3)', 'yes',
                    'carry_verified', '三表→径向/逐平面 opcode', '账本 §26/§41/§43；㉙', 'verified', 'L1')
        return ('镜头校正表', 'OpcodeList3(51022)', 'unknown', 'none', 'no',
                'drop_semantics_unknown', '无权威换算（Samsung/Olympus）', '账本 §44；exiftool 私有通信命名', 'source_inferred', 'L2')
    if re.search(r'^(DistortionCorrectionSetting|VignettingCorrectionSetting|ChromaticAberrationCorrectionSetting|'
                 r'DistortionCorrParamsPresent|DistortionCorrection|DistortionCorrParamsNumber|'
                 r'VignettingCorrection|VignettingSetting)$', n, re.I):
        if 'SAMSUNG' in mk:   # Samsung 的 VignettingCorrection/Setting 是 15 值表，不是开关
            return ('镜头校正表', 'OpcodeList3(51022)', 'unknown', 'none', 'no',
                    'drop_semantics_unknown', '无权威换算（Samsung）', '账本 §44', 'source_inferred', 'L2')
        return ('镜头校正开关', 'opcode 的发射门槛', 'exact', 'ACR', 'yes',
                'carry_verified', '开关=0 才跳过（并点名）', '⑱h/㉙a', 'verified', 'L3')

    # ── 坏点表 ──
    if re.search(r'^SensorDefects$|badpix|hotpix|deadpix', n, re.I):
        return ('坏点/缺陷表', 'OpcodeList1(51008)', 'exact', 'ACR', 'yes',
                'carry_verified', '坐标+类型精确映射', '账本 §27/§28；㉒', 'verified', 'B1')

    # ── 黑电平/量程：厂商值经 LibRaw 进入标准位；逐行/逐列增量单独判过 ──
    if re.search(r'^BlackLevel(Data|Top|Bottom|2)?$|^BlackLevel$|^AverageBlackLevel$|^WhiteLevel$', n, re.I):
        return ('遮罩/黑电平/量程', 'BlackLevel(50714)', 'exact', 'ACR', 'n/a',
                'carry_indirect', 'LibRaw→Standard BlackLevel', '㉓（IIQ/Leaf）+ 产物回读', 'verified', 'P1')
    if re.search(r'WB_.*Levels|WhiteBalance|ColorTemp', n, re.I):
        return ('白平衡', 'AsShotNeutral(50728)', 'mapped', 'ACR', 'n/a',
                'carry_indirect', 'LibRaw cam_mul→CameraNeutral', '产物 AsShotNeutral 回读（本机 3 样本）', 'verified', 'P2')
    if re.search(r'^Sensor(Left|Top|Right|Bottom)Border$', n, re.I):
        return ('裁剪/有效区', 'ActiveArea(50829)', 'exact', 'ACR', 'n/a',
                'carry_indirect',
                '四边界=ActiveArea(top,left,bottom,right)（R6 实测 108/156/2379/3563 逐值相同）',
                '㉚；产物 ActiveArea 回读', 'verified', 'P4')
    if n == 'ImageUniqueID':
        why = ('厂商副本与 ExifIFD 值不同（中间字节 4b vs 00）；ImageUniqueID 不在后置名单，标准位那份也未搬'
               if 'SIGMA' in mk else
               '值只在 [Canon] 组；后置按 ExifIFD 组限定 ⇒ 未搬（加名字前须处理 Sigma 两值不同）')
        return ('机身标识', 'EXIF 0xA420 ImageUniqueID', 'mapped', 'ACR/exiftool', 'n/a',
                'carry_gap', why, '§44 CR3 复核；源/产物回读', 'verified', 'S3')
    if re.search(r'^(MaskedAreas|ActiveArea|DefaultCrop|ImageWidth|ImageHeight|SensorAreas|'
                 r'RawImage(Width|Height|FullWidth|FullHeight|CropTopLeft|CroppedSize|FullSize)|'
                 r'SensorWidth|SensorHeight|'
                 r'PanasonicImage(Width|Height)|CanonImage(Width|Height)|SonyImage(Width|Height)|'
                 r'OlympusImage(Width|Height)|'
                 r'CropLeftMargin|CropRightMargin|CropTopMargin|CropBottomMargin|'
                 r'CroppedImage(Width|Height|Left|Top)|'
                 r'BlackMask(Left|Top|Right|Bottom)Border)$', n, re.I):
        return ('裁剪/有效区', 'ActiveArea(50829)/DefaultCrop(50719/20)', 'mapped', 'ACR', 'n/a',
                'carry_indirect', 'LibRaw 可见区→ActiveArea/DefaultCrop；需逐件审计', '§44 逐件审计', 'pending', 'P3')

    # ── 色彩矩阵（不是"配方"）：DNG 输入复制 / 私有源由 LibRaw cam_xyz 合成 ──
    # 2026-10-23 逐值对撞（_probe-color-matrix-diff.py）：本语料 14 条厂商矩阵与产物
    # ColorMatrix1/2 **都不相等**（最小 max|Δ|=0.38，最大 1.04）⇒ 不能写成"已搬运"。
    # 产物标准位由 LibRaw adobe_coeff（= Adobe ColorMatrix2 标定）填；这批厂商矩阵的
    # 坐标方向/量纲/光源在 ExifTool 里只有名字、没有定义 ⇒ 不冒名、也不猜。
    if re.search(r'^(ColorMatrix|ColorMatrix1|ColorMatrix2|CameraCalibration1|CameraCalibration2|'
                 r'ForwardMatrix1|ForwardMatrix2)$', n, re.I):
        return ('色彩矩阵', 'ColorMatrix1/2(50721/22)', 'unknown', 'ACR', 'n/a',
                'drop_semantics_unknown',
                '厂商组矩阵与产物 CM1/2 均不相等（min max|Δ|=0.38~1.04）；方向/量纲/光源未定义',
                '§44；_probe-color-matrix-diff.py', 'verified', 'C1')
    if re.search(r'^(ColorMatrixA2|ColorMatrixB2)$', n, re.I):
        return ('色彩矩阵（变体）', '未定', 'unknown', 'none', 'no',
                'drop_semantics_unknown',
                'Pentax A2/B2 变体与产物 CM1/2 不相等（max|Δ|≥0.70）；变体语义未定',
                '§44；_probe-color-matrix-diff.py', 'verified', 'C3')
    if re.search(r'^(ColorMatrixSRGB|ColorMatrixAdobeRGB)$', n, re.I):
        return ('色彩设置', '无（JPEG 输出矩阵）', 'none', 'none', 'no',
                'drop_no_carrier', 'JPEG 输出色彩矩阵，不是 raw 矩阵', '§44', 'source_inferred', 'C4')
    if re.search(r'^(ColorMatrixNumber|ColorDataVersion|CoringFilter|ColorCompensationFilter|NDFilter|'
                 r'InternalNDFilter|ColorFilter|BWFilter)$', n, re.I):
        return ('色彩设置', '无（不是矩阵）', 'none', 'none', 'no',
                'drop_no_carrier', '机身设置/版本号，无标准位', '§44 名称级核对', 'source_inferred', 'C2')

    # ── 色调曲线/风格：只有 Sony IDC 的"生效值"已做 ──
    if re.search(r'tone|curve|film|picture_?style|creative|dynamicrange|d_?range|shadowtone|highlighttone|'
                 r'colortone|effect|zone', n, re.I):
        if 'IDC' in n.upper() or ('SONY' in mk and re.search(r'IDC|ProfileToneCurve', n, re.I)):
            return ('色调曲线/生效 look', 'ProfileToneCurve(50940)+OpcodeList2', 'fitted', 'ACR', 'yes',
                    'carry_verified', 'IDC 生效版本', '⑯/⑰', 'verified', 'T1')
        return ('色调曲线/机内风格', 'ProfileToneCurve 仅收生效值', 'unknown', 'none', 'no',
                'drop_semantics_unknown', '菜单预设≠本次生效值，无定位法', '§23/§44', 'pending', 'T2')

    # ── 镜头标识：标准 EXIF 字段在母体外单独搬运；厂商副本仅作重复信息 ──
    if re.search(r'^(LensModel|LensInfo|Lens|LensSpec|LensID)$', n, re.I):
        return ('镜头标识', 'LensModel/LensInfo(50736)/EXIF 0xA432-5', 'mapped', 'ACR/exiftool', 'n/a',
                'info_duplicate_standard', '同值已在标准 EXIF 位', '⑳；产物回读', 'verified', 'I1')
    if re.search(r'lens|focal|zoom', n, re.I):
        return ('镜头标识（厂商副本）', '无（标准位由 EXIF 另搬）', 'none', 'none', 'no',
                'drop_no_carrier', '厂商副本无独立载体；信息若只在此处则丢', '§44', 'source_inferred', 'I2')
    if re.search(r'^ShootingMode$', n, re.I):
        return ('拍摄模式', '无', 'none', 'none', 'no',
                'drop_no_carrier', 'EXIF 无对应位', '§44', 'source_inferred', 'I3')

    # ── 机身标识/计数 ──
    if re.search(r'^SerialNumber$|SerialNumberFormat|^LensSerialNumber$', n, re.I):
        return ('机身标识', 'EXIF 0xA431', 'mapped', 'ACR/exiftool', 'n/a',
                'info_duplicate_standard', '标准位另搬', '⑳', 'verified', 'S1')
    if re.search(r'InternalSerialNumber|Firmware|ReleaseMode|ModelReleaseYear|counter|shots|owner|device|body', n, re.I):
        return ('机身标识/计数（厂商私有）', '无', 'none', 'none', 'no',
                'drop_no_carrier', '内部序号/固件/计数无标准位', '§44', 'source_inferred', 'S2')

    # ── 厂商组里的"标准字段副本"：同值在 ExifIFD（母体外）另有一份并被产品搬运 ──
    if re.search(r'^(ExposureTime|FNumber|ISO|ExposureCompensation|MeteringMode|FlashMode|'
                 r'FlashExposureComp|FocusMode|ColorSpace|Rotation|ImageStabilization|DriveMode|'
                 r'ExposureProgram|ExposureMode|CameraOrientation|MaxAperture|Rating)$', n, re.I):
        lvl = 'verified' if re.search(r'^(ExposureTime|FNumber|ISO)$', n, re.I) else 'source_inferred'
        return ('标准字段的厂商副本', 'ExifIFD（母体外）同值', 'mapped', 'ACR/exiftool', 'n/a',
                'info_duplicate_standard', '信息量不丢：标准位另有一份并被后置搬运',
                '⑳a/⑳p（部分字段逐值核过）', lvl, 'X1')

    # ── 机内 JPEG 风格/显示设置：不改变 raw 像素，也无标准位 ──
    if re.search(r'^(ColorMode|Sharpness|Saturation|Contrast|Quality|ISOSetting|TextStamp|'
                 r'FacesDetected|SharpnessOvershoot|SharpnessUndershoot|LiveViewShooting|'
                 r'SequenceNumber|Accelerometer[XYZ])$', n, re.I):
        return ('机内风格/显示设置', '无（不影响 raw 像素）', 'none', 'none', 'no',
                'drop_no_carrier', 'JPEG/显示侧设置，无 RAW 语义载体', '§44', 'source_inferred', 'X2')

    # ── 源容器内部布局：产物必然按自己的布局重写，不是信息损失 ──
    if re.search(r'^(StripOffsets|StripByteCounts|BitsPerSample|BitDepth|FileFormat|FujiLayout)$', n, re.I):
        return ('容器内部布局', '产物自写布局', 'n/a', 'n/a', 'no',
                'policy_discarded', '源容器结构；像素数据另搬，产物重写布局', '§44', 'source_inferred', 'X4')

    # ── 机身状态/机内设置：无标准位 ──
    if re.search(r'^(SensorTemperature|CameraTemperature|ShutterCount|BatteryLevel|SelfTimer|BurstShot|'
                 r'AutoBracketing|Macro|MacroMode|SceneMode|PictureMode|PictureProfile|IntelligentAuto|'
                 r'Shadow|Highlight|ExposureAdjust|RawExposureBias|X3FillLight|WBShiftAB|WBShiftGM|WBMode|'
                 r'MinoltaQuality|Quality2|SequenceImageNumber|BabyAge)$', n, re.I):
        return ('机身状态/机内设置', '无', 'none', 'none', 'no',
                'drop_no_carrier', '机内设置/状态，无标准位或与 raw 渲染无关', '§44', 'source_inferred', 'X5')

    # ── 平场/校准数据：语义与载体都未定，不猜 ──
    if re.search(r'^(AllColorFlatField2|RedBlueFlatField|BlackPoint|WhitePoint|GrayPoint)$', n, re.I):
        return ('校准/平场数据', '未定', 'unknown', 'none', 'no',
                'unaudited', '平场/校准数据的语义与载体未定', '§44（待逐块审计）', 'pending', 'X3')

    # ── AF/AE 与降噪：无载体 ──
    if re.search(r'af_?point|affocus|focus_?point|af_?area|afm|awb|afpattern|^point|'
                 r'^AFTrackingSensitivity$|^OneShotAFRelease$|^ShutterReleaseWithoutLens$|'
                 r'^AFImage(Width|Height)$|^AIServo(First|Second)Image$', n, re.I):
        return ('AF/AE 点阵', '无', 'none', 'none', 'no',
                'drop_no_carrier', 'EXIF/DNG 均无整组点位', '§23', 'source_inferred', 'A1')
    if re.search(r'noise|nr_?|noisereduction', n, re.I):
        return ('噪声/降噪旋钮', 'NoiseProfile 只收测量值', 'none', 'none', 'no',
                'drop_no_carrier', '开关/强度无载体', '§23', 'source_inferred', 'N1')

    # ── 预览：JPEG 原字节搬成产物预览 ──
    if re.search(r'^(PreviewImage|ThumbnailImage|JpgFromRaw|OtherImage|PreviewImageSize|ThumbnailOffset|'
                 r'ThumbnailLength|PreviewImageStart|PreviewImageLength|IDCPreviewStart|IDCPreviewLength)$', n, re.I):
        return ('预览/图像字节', '产物预览 IFD', 'exact', 'ACR/exiftool', 'n/a',
                'carry_verified', 'JPEG 原字节逐字节搬运（偏移/长度按产物重算）', '⑳j；㉖b', 'verified', 'V1')
    if re.search(r'^(ThumbnailImageValidArea|DustRemovalData|SceneDetectData|RawData|JPEGQuality)$', n, re.I):
        return ('预览/图像字节（附属）', '无独立标准位', 'none', 'none', 'no',
                'drop_no_carrier', '缩略图附属/除尘数据/JPEG 质量，无 RAW 语义载体', '§44', 'source_inferred', 'V2')

    # ── 时间 ──
    if re.search(r'date|time_?zone|timezone|gmt|dst', n, re.I):
        return ('时间/时区', 'EXIF 0x9010-12/XMP', 'mapped', 'ACR/exiftool', 'n/a',
                'info_duplicate_standard', '标准位另搬（部分）', '⑰/⑳', 'verified', 'D1')

    # ── 厂商未命名/整块：不猜语义，也不断言无价值 ──
    if re.search(r'^(MakerNoteVersion)$', n, re.I):
        return ('厂商注记元信息', '无', 'none', 'none', 'no',
                'policy_discarded', '注记版本号本身无用户语义', '§19 政策', 'source_inferred', 'U1')
    if re.search(r'^(Unknown_0x|Sony_SR2DataIFD_0x|Sony_SR2SubIFD_0x|Samsung_Type2_0x|Tag\d+|Unknown_|Sony_SR2Private)', n, re.I):
        return ('未命名/不透明私有块', '可恢复路径：50828 嵌入原件 / SR2Private', 'none', 'none', 'no',
                'unaudited', '名称不含语义；不据此断言无价值', '§44（待逐块审计）', 'pending', 'U2')
    return ('未归类', '未审计', 'none', 'none', 'no',
            'unaudited', '规则表未覆盖，记未审计', '§44', 'pending', 'X0')


def _dims(v):
    """把 exiftool 的值串解析成整数列表（"2944x2144" / "14 64" / "1936"）。"""
    if v is None:
        return []
    return [int(x) for x in re.findall(r'-?\d+', str(v))]


def load_crop_map(path):
    """读 _probe-dng-crop-map.py 的 CSV；缺失时返回 {}（裁剪类退回 pending，不假装已审计）。"""
    if not path or not os.path.isfile(path):
        return {}
    run = os.path.basename(os.path.dirname(os.path.abspath(path)))
    out = {}
    with open(path, encoding='utf-8-sig') as fh:
        for r in csv.DictReader(fh):
            r['run'] = run
            out[os.path.splitext(r['name'])[0]] = r
    return out


def crop_override(name, value, stem, cm):
    """裁剪类的逐件裁决：证据来自 ㉚（写侧+Adobe 渲染+LibRaw 解析）与 crop-map 三侧读数。

    只做**能指到读数**的判断；对不上的一律返回 None（保持 pending），不按名字猜。
    """
    r = cm.get(stem)
    if not r:
        return None
    verdict = r.get('verdict', '')
    ev = '㉚；crop-map %s' % r.get('run', '')
    dims = _dims(value)
    cw, ch = (_dims(r.get('prod.DefaultCropSize')) + [0, 0])[:2]
    ox, oy = (_dims(r.get('prod.DefaultCropOrigin')) + [0, 0])[:2]
    aw, ah = int(r.get('vis_w') or 0), int(r.get('vis_h') or 0)
    rw, rh = int(r.get('raw_w') or 0), int(r.get('raw_h') or 0)

    if 'BlackMask' in name:
        return ('drop_no_carrier', '掩膜边界；非 DNG 源不写 MaskedAreas（本机样本值 0）', ev, 'verified')
    if name in ('CroppedImageLeft', 'CroppedImageTop'):
        if verdict == 'mapped':
            return ('policy_discarded',
                    '本字段与 SensorInfo+机身 JPEG 对拍冲突（最优原点=%d,%d）⇒ 取后者' % (ox, oy),
                    ev, 'verified')
        return ('drop_no_carrier', '裁剪原点未被采用（%s）' % verdict, ev, 'verified')
    if name == 'RawImageCropTopLeft':
        if verdict == 'mapped':
            return ('carry_verified', '厂商裁剪原点 → DefaultCropOrigin %d,%d' % (ox, oy), ev, 'verified')
        return ('drop_semantics_unknown', '厂商原点未被采用（%s）' % verdict, ev, 'verified')
    if name in ('CroppedImageWidth', 'CroppedImageHeight', 'CanonImageWidth', 'CanonImageHeight',
                'PanasonicImageWidth', 'PanasonicImageHeight', 'RawImageCroppedSize'):
        if verdict == 'mapped' and dims and cw:
            if name == 'RawImageCroppedSize':
                ok = len(dims) >= 2 and dims[0] == cw and dims[1] == ch
            elif name.endswith('Height'):
                ok = dims[0] == ch
            else:
                ok = dims[0] == cw
            if ok:
                return ('carry_verified', '厂商裁剪尺寸=产物 DefaultCrop %dx%d' % (cw, ch), ev, 'verified')
        return ('drop_semantics_unknown', '厂商裁剪未进 DefaultCrop（%s）' % verdict, ev, 'verified')
    if re.match(r'^Crop(Left|Right|Top|Bottom)Margin$', name):
        return ('drop_semantics_unknown',
                '厂商 margin 标签与已验证裁剪框（%dx%d@%d,%d）不相容；ExifTool 自注 "(NC, may be right)" ⇒ 不采信'
                % (cw, ch, ox, oy), ev, 'verified')
    if name in ('SonyImageWidth', 'SonyImageHeight'):
        return ('drop_semantics_unknown',
                '厂商只给宽高（0xb02b）、无原点；拒绝猜原点并保持满幅', ev, 'verified')
    if name == 'SensorAreas':
        return ('drop_semantics_unknown',
                'full/valid 两组字段序只有 ExifTool 一行注释（"full and valid sensor areas"），无官方定义；产品不解析',
                ev, 'source_inferred')
    if name in ('OlympusImageWidth', 'OlympusImageHeight'):
        return ('drop_semantics_unknown',
                '厂商给 2240x1680 但无原点；可见区 2256x1684 的边框含图像内容（非黑）⇒ 不猜裁剪',
                ev, 'verified')
    if name in ('RawImageFullSize', 'RawImageFullWidth', 'RawImageFullHeight',
                'SensorWidth', 'SensorHeight'):
        if dims == [rw, rh] or (name.endswith('Width') and dims[:1] == [rw]) or \
           (name.endswith('Height') and dims[:1] == [rh]):
            return ('carry_indirect', '存储幅面=产物 ImageWidth/Height', ev, 'verified')
        if name in ('SensorWidth', 'SensorHeight'):
            return ('drop_semantics_unknown', '厂商"传感器幅面"与 LibRaw raw 幅面不一致，未定位', ev, 'pending')
    if name in ('ImageWidth', 'ImageHeight', 'RawImageWidth', 'RawImageHeight'):
        if dims == [aw, ah] or (name.endswith('Width') and dims[:1] == [aw]) or \
           (name.endswith('Height') and dims[:1] == [ah]):
            return ('carry_indirect', '可见区=产物 ActiveArea', ev, 'verified')
        if dims == [rw, rh] or (name.endswith('Width') and dims[:1] == [rw]) or \
           (name.endswith('Height') and dims[:1] == [rh]):
            return ('carry_indirect', '存储幅面=产物 ImageWidth/Height', ev, 'verified')
        # 厂商给了更小的"图像尺寸"、但 LibRaw 没解析成内嵌裁剪（Minolta/Sigma/Samsung/E-10）
        return ('drop_semantics_unknown',
                '厂商图像尺寸 %s 小于可见区 %dx%d，但无原点/未被子解析 ⇒ 不猜' % (value, aw, ah),
                ev, 'source_inferred')
    return None


def corpus():
    rows = []
    for d in DIRS:
        if not os.path.isdir(d):
            continue
        for f in sorted(os.listdir(d)):
            p = os.path.join(d, f)
            if not f.lower().endswith(EXT) or not os.path.isfile(p) or os.path.getsize(p) < 20000:
                continue
            if DERIVED.search(os.path.splitext(f)[0]):
                continue
            rows.append(p)
    return rows


def color_override(name, value):
    """色彩设置类：逐值判"有没有图像效果"，避免把 CoringFilter=1792 这种非零值写成"无影响"。"""
    v = (value or '').strip().lower()
    if name in ('NDFilter', 'InternalNDFilter', 'ColorCompensationFilter', 'ColorFilter', 'BWFilter'):
        if v in ('0', 'off', 'n/a', 'none', 'no'):
            return ('drop_no_carrier', '值为 %s ⇒ 无图像效果（也无需载体）' % value,
                    '§44 逐值核对', 'source_inferred')
        return ('drop_no_carrier', '非零机内滤镜/补偿设置，DNG 无对应位', '§44 逐值核对', 'source_inferred')
    if name == 'CoringFilter':
        return ('drop_no_carrier', '降噪阈值（本机 %s），DNG 无 coring 位；渲染细节会与机内不同' % value,
                '§44 逐值核对', 'source_inferred')
    if name in ('ColorMatrixNumber', 'ColorDataVersion'):
        return ('drop_no_carrier', '矩阵编号/数据版本，不是矩阵本身', '§44 逐值核对', 'source_inferred')
    if name in ('ColorMatrixSRGB', 'ColorMatrixAdobeRGB'):
        return ('drop_no_carrier', 'JPEG 输出色彩矩阵，不是 raw 矩阵（与 C1 的 ColorMatrix 不同）',
                '§44 逐值核对', 'source_inferred')
    return None


def default_crop_map():
    base = os.path.join(R, 'tests', 'output', 'dng-crop-map')
    if not os.path.isdir(base):
        return ''
    runs = sorted(d for d in os.listdir(base)
                  if os.path.isfile(os.path.join(base, d, 'crop-map.csv')))
    return os.path.join(base, runs[-1], 'crop-map.csv') if runs else ''


def scan(outdir, crop_map_path=None):
    os.makedirs(outdir, exist_ok=True)
    ledger = []
    files = corpus()
    cm = load_crop_map(crop_map_path)
    for p in files:
        meta = subprocess.run([EX, '-s3', '-Make', '-Model', p], capture_output=True, text=True,
                              errors='replace').stdout.splitlines()
        make = meta[0].strip() if len(meta) > 0 else ''
        model = meta[1].strip() if len(meta) > 1 else ''
        out = subprocess.run([EX, '-a', '-u', '-G1', '-s', '-e', p], capture_output=True,
                             text=True, errors='replace').stdout
        for line in out.splitlines():
            m = re.match(r'^\[([^\]]+)\]\s+(\S+)\s*:\s(.*)$', line.strip())
            if not m:
                continue
            g, name, val = m.group(1), m.group(2), m.group(3).strip()
            if any(g.startswith(x) for x in STD):
                continue
            (cls, carrier, mp, reader, apply_, disp, why, ev, lvl, rid) = rule(name, make)
            if cls == '裁剪/有效区':
                ov = crop_override(name, val, os.path.splitext(os.path.basename(p))[0], cm)
                if ov:
                    disp, why, ev, lvl = ov
            elif cls.startswith('色彩'):
                ov = color_override(name, val)
                if ov:
                    disp, why, ev, lvl = ov
            ledger.append(dict(
                file=os.path.relpath(p, R), make=make, model=model, group=g, tag=name,
                value=val[:160].replace('\n', ' '), value_sha8=hashlib.sha256(val.encode('utf-8', 'replace')).hexdigest()[:8],
                cls=cls, carrier=carrier, map=mp, reader=reader, apply=apply_,
                disposition=disp, reason=why, evidence=ev, level=lvl, rule=rid,
                b_recoverable='yes(50828；仅 -embed-raw，默认关)'))
    with open(os.path.join(outdir, 'ledger.csv'), 'w', newline='', encoding='utf-8') as fh:
        w = csv.DictWriter(fh, fieldnames=list(ledger[0].keys()))
        w.writeheader()
        w.writerows(ledger)
    summary = summarize(ledger, files)
    with open(os.path.join(outdir, 'summary.json'), 'w', encoding='utf-8') as fh:
        json.dump(summary, fh, ensure_ascii=False, indent=2)
    return summary


def summarize(ledger, files=None):
    total = len(ledger)
    by_disp = collections.Counter(r['disposition'] for r in ledger)
    by_cls = collections.Counter(r['cls'] for r in ledger)
    by_level = collections.Counter(r['level'] for r in ledger)
    by_rule = collections.Counter(r['rule'] for r in ledger)
    by_make = collections.Counter(r['make'] for r in ledger)
    # 每个处置下面按类的交叉表，便于审计"类级结论"是否成立
    cross = collections.defaultdict(collections.Counter)
    for r in ledger:
        cross[r['disposition']][r['cls']] += 1
    # 一致性：处置之和 == 总数；等级之和 == 总数；已审计 = 非 unaudited
    s = {
        'files': len(files) if files else len({r['file'] for r in ledger}),
        'total_instances': total,
        'by_disposition': dict(by_disp),
        'by_class': dict(by_cls.most_common()),
        'by_evidence_level': dict(by_level),
        'by_rule': dict(by_rule.most_common()),
        'by_make': dict(by_make.most_common()),
        'cross': {k: dict(v.most_common()) for k, v in cross.items()},
        'sum_check': {
            'dispositions_sum': sum(by_disp.values()),
            'levels_sum': sum(by_level.values()),
            'classes_sum': sum(by_cls.values()),
            'all_equal_total': (sum(by_disp.values()) == total == sum(by_level.values()) == sum(by_cls.values())),
        },
        'audited_instances': total - by_disp.get('unaudited', 0),
        'audited_share': round((total - by_disp.get('unaudited', 0)) / total, 4) if total else 0.0,
    }
    assert set(by_disp) <= set(DISPOSITIONS), '未知处置'
    assert set(by_level) <= set(LEVELS), '未知证据等级'
    return s


def verify(dirpath):
    with open(os.path.join(dirpath, 'ledger.csv'), encoding='utf-8') as fh:
        ledger = list(csv.DictReader(fh))
    with open(os.path.join(dirpath, 'summary.json'), encoding='utf-8') as fh:
        old = json.load(fh)
    new = summarize(ledger)
    ok = True
    for k in ('total_instances', 'by_disposition', 'by_class', 'by_evidence_level', 'audited_instances'):
        if new[k] != old[k]:
            print('MISMATCH %s: ledger=%r summary=%r' % (k, new[k], old[k]))
            ok = False
    if not new['sum_check']['all_equal_total']:
        print('MISMATCH 汇总不自洽: %r' % (new['sum_check'],))
        ok = False
    print('VERIFY %s: total=%d audited=%d unaudited=%d 自洽=%s' %
          ('OK' if ok else 'FAIL', new['total_instances'], new['audited_instances'],
           new['by_disposition'].get('unaudited', 0), new['sum_check']['all_equal_total']))
    return 0 if ok else 1


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=os.path.join(R, 'tests', 'output', 'dng-audit', 'latest'))
    ap.add_argument('--verify', default='')
    ap.add_argument('--crop-map', default=default_crop_map(),
                    help='_probe-dng-crop-map.py 的 crop-map.csv（缺省取 tests/output/dng-crop-map 最新一轮）')
    a = ap.parse_args()
    if a.verify:
        sys.exit(verify(a.verify))
    s = scan(a.out, a.crop_map)
    s['crop_map'] = os.path.relpath(a.crop_map, R) if a.crop_map else '(absent)'
    with open(os.path.join(a.out, 'summary.json'), 'w', encoding='utf-8') as fh:
        json.dump(s, fh, ensure_ascii=False, indent=2)
    print('out=%s files=%d total=%d audited=%d (%.1f%%)' %
          (a.out, s['files'], s['total_instances'], s['audited_instances'], 100 * s['audited_share']))
    for k in DISPOSITIONS:
        print('  %-26s %5d' % (k, s['by_disposition'].get(k, 0)))
    print('  自洽:', s['sum_check'])
