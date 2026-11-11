# Bridle

Horseman için lab uygulaması: HSL kodunu komut satırı olmadan dener, Horseman'ın farklı derlemelerini yan yana yükleyip aşama aşama karşılaştırır.

## Çalıştırma

`run.cmd` dosyasına çift tıklayın, ya da bir komut satırından:

```
.\run.cmd
```

.NET 10 SDK gerekir. İlk derlemede Avalonia paketleri NuGet'ten indirilir.

## Nasıl kullanılır

Pencere üç bölümdür:

- **Sol:** yüklü sürümler ve iki seçici. **A** gösterilen sürümdür; **B** seçilirse aynı kod iki sürümden de geçirilir.
- **Orta:** kaynak ve girdiler. Girdiler her satıra bir tane yazılır: `data=42,-7,19`.
- **Sağ:** her aşama için bir sekme (Sözcükler, Ağaç, Denetim, Çalıştır, Bytecode). B seçiliyse iki çıktı yan yana durur, farklı satırlar boyanır ve çıktısı farklı olan sekmenin başlığında `≠` görünür.

Yazdıkça kendiliğinden çalışır; `F5` elle çalıştırır. Bir tanıya tıklamak kaynakta o satıra götürür.

### Bir değişikliği eskisiyle karşılaştırmak

Örneğin lexer'ı değiştirmeden önce ve sonra:

1. Bridle açıkken, "canlı" sürümün kartındaki **⎘** düğmesine basın. O anki derleme `versions` klasörüne kopyalanır ve B'ye yerleşir.
2. Horseman'da kodu değiştirip yeniden derleyin (`scripts\build.cmd`).
3. Bridle canlı DLL'in değiştiğini görür, yeniden yükler ve yeniden çalıştırır. Hangi aşamaların çıktısı değiştiyse sekmesinde `≠` belirir.

Elle de eklenebilir: **DLL ekle…** ile herhangi bir `horseman_bridle.dll` seçilir, ya da dosya `versions` klasörüne bırakılır.

## Nasıl çalışır

- Her sürüm bir **DLL**'dir ve [native/include/bridle.h](native/include/bridle.h) içindeki ABI'yi dışa açar. ABI dardır: içeri kaynak metni girer, dışarı JSON çıkar. Horseman'ın iç yapıları sınırı geçmediği için eski ve yeni derlemeler aynı Bridle ile konuşabilir.
- Sekmeler DLL'den gelir (`bridle_describe`). Horseman'a yeni bir aşama eklendiğinde (örneğin VM) Bridle'da değişiklik gerekmez; sekme kendiliğinden çıkar.
- Her DLL **ayrı bir süreçte** çalışır (`Bridle.exe --host`). Deneysel bir derleme çökerse ya da sonsuz döngüye girerse yalnızca o süreç ölür; Bridle çıkış kodunu gösterir ve bir sonraki çalıştırmada süreci yeniden başlatır. Aşama başına süre sınırı 5 saniyedir.
- DLL'ler yüklenmeden önce geçici bir klasöre kopyalanır; özgün dosya kilitlenmez, bu yüzden Bridle açıkken Horseman yeniden derlenebilir.

## Arayüzsüz kipler

```
Bridle.exe --selftest <dll> [kaynak.hm] [input:ad=1,2 ...]
Bridle.exe --render <çıktı.png> [--dll <dll>]... [--source <kaynak.hm>] [--inputs "a=1;b=2"]
```

`--selftest` her aşamayı çalıştırıp sonucu yazar. `--render` pencereyi ekrana çıkarmadan PNG'ye çizer; arayüz değişikliklerini görmek için kullanılır.

## Dizin

```
native/include/bridle.h   ABI (asıl kopya Horseman deposunda: bridle/include/bridle.h)
src/Bridle/               Avalonia uygulaması (.NET 10)
versions/                 karşılaştırma için saklanan derlemeler
```

Ayarlar `%APPDATA%\Bridle\settings.json` içinde tutulur.
