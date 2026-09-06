# Local View B3D / B3D Publisher

Локальный Publisher для БАЗИС 24. Production-задача — передать **уже рассчитанную БАЗИС геометрию модели** в один автономный HTML-файл для заказчика, не реконструируя B3D и не используя скрипты БАЗИС.

## Production-маршрут

```text
B3D-Publisher.exe
  → штатный БАЗИС-Просмотр 3D (ViewerX.exe / Viewer24.exe)
  → Viewer3D открывает исходный B3D
  → штатная команда Viewer3D «Сохранить»
  → временный VRML (*.wrl)
  → Publisher читает IndexedFaceSet/материалы/UV экспортированного Viewer3D mesh
  → один <имя модели>_просмотр.html
  → временный WRL удаляется
```

Ключевой принцип: **B3D интерпретирует БАЗИС, а не Publisher**. Publisher не строит панели, пазы, спилы, вырезы или другие мебельные операции по данным B3D. Временный WRL является только штатным mesh-экспортом БАЗИС-Просмотр 3D и никогда не является клиентским deliverable.

В production **не используются**:

- BAZIS Script API и папка `Scripts`;
- реконструкция геометрии из B3D;
- собственное построение панелей/обработок;
- OBJ, 3DS или DAE как промежуточный формат;
- WebViewer cloud или другая облачная служба;
- CFRN/WebViewer DLL как production-маршрут;
- обход лицензирования или ограничений БАЗИС;
- внешние JavaScript/CSS/CDN-зависимости итогового HTML.

## Почему Viewer3D

БАЗИС-Просмотр 3D является штатной утилитой комплекса БАЗИС. Она открывает модели БАЗИС и предоставляет сохранение текущей 3D-модели в VRML. Поэтому production Publisher использует Viewer3D как официальный локальный мост от B3D к уже рассчитанной полигональной сцене, не требуя Script API.

`Viewer3DExporter.cs` автоматически:

1. ищет установленный `Viewer24.exe`/`ViewerX.exe` рядом с БАЗИС, через App Paths и каталоги установки;
2. запускает Viewer3D с выбранным `.b3d`;
3. вызывает штатную команду «Сохранить»;
4. в стандартном диалоге Windows выбирает VRML;
5. сохраняет WRL во временный каталог;
6. после публикации временный каталог полностью удаляется.

## Что получает заказчик

Только один файл:

```text
<имя модели>_просмотр.html
```

Заказчику не нужны БАЗИС, B3D-Publisher, Viewer3D, плагины, отдельные текстуры, сервер или интернет. HTML открывается локально в современном браузере с WebGL.

## Геометрия

`VrmlParser.cs` читает только результат штатного Viewer3D VRML-экспорта:

- `Shape`;
- `IndexedFaceSet`;
- `Coordinate.point`;
- `coordIndex`;
- `Normal.vector` / `normalIndex`;
- `TextureCoordinate.point` / `texCoordIndex`;
- `ccw` / `normalPerVertex`.

Полигональные грани VRML переводятся в треугольники для WebGL. Совпадающие записи вершины индексируются только при полном совпадении позиции, нормали и UV, поэтому жёсткие грани и текстурные швы сохраняются.

Publisher не открывает структуру B3D и не пытается повторять геометрическое ядро БАЗИС.

## Материалы и текстуры

Из штатного VRML используются `Material.diffuseColor`, `ImageTexture.url` и `TextureCoordinate`. Если Viewer3D экспортировал ссылку на доступное локальное растровое изображение, Publisher встраивает его в HTML как `data:` URI.

Поддерживаемые растровые форматы: PNG, JPEG, WebP, GIF и BMP. Если пригодная локальная текстура отсутствует, остаётся экспортированный цвет поверхности; текстуры по догадке не назначаются.

## Вьювер

`OfflineHtmlPublisher.cs` создаёт собственный полностью автономный WebGL viewer:

- светлый фон без сетки;
- спокойное orbit-вращение без авто-анимации;
- панорамирование и масштабирование;
- «Вписать»;
- характерные рёбра через `gl.LINES` с подавлением копланарных диагоналей;
- отдельный режим прозрачности;
- GPU picking через `gl.readPixels`;
- «Снять выделение» и `Esc`;
- индексированная геометрия через `gl.drawElements`;
- WebGL2 и WebGL1 fallback;
- материалы и встроенные текстуры без сетевых запросов.

## B3D-Publisher.exe

Windows-host находится в:

```text
host/B3DPublisherHost/
```

Рабочий порядок:

1. выбрать `.b3d`;
2. Publisher автоматически запускает штатный Viewer3D;
3. Viewer3D создаёт временный `.wrl`;
4. Publisher пакует mesh/материалы/текстуры в HTML;
5. проверяет, что HTML не содержит внешних `script src`, `http://` или `https://` зависимостей;
6. удаляет временный WRL;
7. показывает количество треугольников, размер HTML и SHA-256.

Рядом с моделью остаётся только клиентский HTML. Никакие служебные sidecar-файлы не создаются.

## Сборка и release gate

GitHub Actions собирает self-contained single-file Windows x64 executable:

```text
B3D-Publisher.exe
SHA256.txt
```

Перед выпуском workflow проверяет:

- наличие Viewer3D → WRL production pipeline;
- наличие VRML `IndexedFaceSet`, UV, normal и material обработки;
- отсутствие Script API в production-host;
- отсутствие B3D-реконструкции в production-host;
- отсутствие OBJ/3DS/DAE production route;
- отсутствие WebViewer/CFRN/cloud route;
- offline/self-contained контракт HTML;
- UX-контракт viewer;
- успешную `.NET` single-file Windows сборку.

## Исследовательские каталоги

В репозитории сохранены более ранние исследования формата B3D:

```text
parser/
geometry/
publisher/
docs/
samples/
```

Они используются только для исследований и исторических regression-тестов. **Production B3D-Publisher их не вызывает и геометрию клиентского HTML из них не получает.**

## Проверка репозитория

```bash
python -m unittest discover -s tests -v
```

Windows production build выполняется workflow `Build Windows B3D Publisher bridge`.
