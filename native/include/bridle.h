#ifndef BRIDLE_H
#define BRIDLE_H

/*
 * Bridle ABI — Horseman ile Bridle lab uygulaması arasındaki sözleşme.
 *
 * Bridle, Horseman'ın farklı derlemelerini (her biri bu ABI'yi dışa açan
 * bir DLL) yan yana yükler, aynı kaynağı hepsinden geçirir ve aşama aşama
 * sonuçları karşılaştırır.
 *
 * ABI bilerek dardır: içeri metin girer, dışarı JSON çıkar. Derleyicinin iç
 * yapıları (sözcükler, ağaç) sınırı geçmez; böylece iç yapılar değişse de
 * eski ve yeni derlemeler aynı Bridle ile konuşabilir.
 *
 * Bu DLL yalnızca geliştirme aracıdır: TEE içinde çalışmaz, TCB'nin parçası
 * değildir.
 *
 * Bu dosyanın asıl kopyası Horseman deposundadır (bridle/include/bridle.h);
 * Bridle deposundaki kopya bununla aynı tutulur.
 */

#include <stdint.h>

/* DLL dışa aktarım tablosuna ekler. */
#if defined(_WIN32)
#  if defined(BRIDLE_BUILD)
#    define BRIDLE_API __declspec(dllexport)
#  else
#    define BRIDLE_API __declspec(dllimport)
#  endif
#else
#  define BRIDLE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ABI sürümü. İmzalar ya da JSON alanlarının anlamı değişirse artırılır. */
#define BRIDLE_ABI_VERSION 1

/* Bu DLL'in derlendiği ABI sürümünü döndürür. */
BRIDLE_API int32_t bridle_abi_version(void);

/*
 * Derlemeyi tanıtan JSON (statik; serbest bırakılmaz):
 *
 *   { "name": "horseman", "version": "0.0.1", "build": "33e041c-dirty",
 *     "built": "Nov 11 2026 14:02:11", "compiler": "MSVC 1944",
 *     "stages": [ { "id": "tokens", "title": "Sozcukler" }, ... ] }
 *
 * "stages", bu derlemenin bridle_run ile çalıştırabildiği aşamalardır;
 * Bridle sekmelerini bu listeden kurar.
 */
BRIDLE_API const char *bridle_describe(void);

/*
 * Kaynağı verilen aşamadan geçirir ve sonucu tek satırlık JSON olarak
 * döndürür:
 *
 *   { "ok": true,
 *     "text": "...",            aşamanın okunur çıktısı; sürümler arası
 *                               karşılaştırılan şey budur
 *     "summary": "...",         tek satırlık özet (boş olabilir)
 *     "diagnostics": [ { "code": "E0207", "line": 9, "column": 12,
 *                        "message": "..." } ] }
 *
 * stage      : bridle_describe'ın bildirdiği bir aşama kimliği.
 * source     : kaynak metin; NUL ile bitmek zorunda değil.
 * options    : NUL ile biten, satır satır "anahtar:değer" (NULL olabilir):
 *                input:ad=1,2,3     girdi (her girdi için bir satır)
 *                gas:100000         gas sınırı
 *                memory:65536       bellek sınırı (hücre)
 *              Tanınmayan anahtarlar yok sayılır.
 *
 * Dönen işaretçi bridle_free ile serbest bırakılır. Bellek yetmezse NULL.
 * Bilinmeyen aşama bir hata değil, "ok": false olan geçerli bir yanıttır.
 */
BRIDLE_API char *bridle_run(const char *stage, const char *source,
                            uint32_t source_len, const char *options);

BRIDLE_API void bridle_free(char *text);

#ifdef __cplusplus
}
#endif

#endif
