// ═══════════════════════════════════════════════════════════
//  ms_compat_shim.c — 提供 libjxl 0.8 (旧 MSVC 编译) 缺失的
//  __imp_* 数学函数符号 (modff/truncf/lroundf/rintf 等)
//  这些函数在 UCRT 中是 __cdecl, 但旧版 lib 引用 __imp_ 前缀
//  直接转发到标准函数即可。
// ═══════════════════════════════════════════════════════════
#include <math.h>

/* __imp_modff: 双精度版本 */
double __cdecl __imp_modff(double x, double* iptr) { return modf(x, iptr); }
float  __cdecl __imp_modff_f(float x, float* iptr) { return modff(x, iptr); }

float __cdecl __imp_truncf(float x) { return truncf(x); }
float __cdecl __imp_lroundf(float x) { return lroundf(x); }
float __cdecl __imp_rintf(float x) { return rintf(x); }
float __cdecl __imp_cbrtf(float x) { return cbrtf(x); }
float __cdecl __imp_copysignf(float x, float y) { return copysignf(x, y); }
double __cdecl __imp_rint(double x) { return rint(x); }
long __cdecl __imp_lround(double x) { return lround(x); }
double __cdecl __imp_remainder(double x, double y) { return remainder(x, y); }
