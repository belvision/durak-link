# Durak Link

Экспериментальный инструмент для игры в «Дурака» на iPhone с Windows. AirPlay передаёт изображение на ПК, а Durak Link отправляет касания и перетаскивания через Bluetooth LE HID. Локальный учёт рассчитан на подкидного дурака с 36 картами и тремя игроками.

- Панель управления открывает список столов, выбирает первую строку и отправляет «Готов». После входа есть автоожидание кнопки по локальному OCR.
- Команда `shot` переносит карту из руки на стол. Ей нужны свежие координаты указателя, карты и цели. Успешная отправка Bluetooth подтверждает доставку команды, а засчитанный ход проверяется по экрану.
- Окно соперников показывает известные карты, вероятные варианты и компактный отбой. SQLite хранит подтверждённые события, решения и результаты партий. Состав скрытых рук оценивается по доступным наблюдениям.
- Локальный наблюдатель `--wait-phone-turn` ищет кнопку действия в нижней части окна AirPlay. Распознавание карт по шаблонам экспериментальное: нужны реальные эталоны и настроенные области. Полностью автоматическая игра и автоматический учёт всей раздачи пока не реализованы.

## Запуск

Понадобятся Windows 10 версии 2004 или новее, .NET SDK 9, совместимый Bluetooth LE адаптер и [UxPlay для Windows](https://github.com/leapbtw/uxplay-windows). Для автоожидания нужен установленный модуль распознавания русского текста Windows. Node.js нужен для программного клиента.

```powershell
dotnet build mouse/DurakMouse.csproj -p:OutputType=WinExe -o build
$env:DURAK_UXPLAY_EXE = 'C:/Tools/uxplay-windows/uxplay-windows.exe'
.\Start-DurakLink.ps1
.\build\DurakMouse.exe --advertise --manual
```

На iPhone выберите «Повтор экрана → Durak-iPhone», введите показанный код AirPlay и подключите Bluetooth-мышь Windows. Используйте один процесс Durak Link с Bluetooth на компьютер. Для программного режима вместо ручной панели запустите `dotnet build/DurakMouse.dll --advertise` или клиент `agent-session.cjs`.

Состояние хранится в `%LOCALAPPDATA%/DurakLink`. Чтобы выбрать другой каталог, установите `DURAK_LINK_HOME`. Переменная `DURAK_LINK_DLL` указывает клиенту путь к другой сборке. UxPlay устанавливается отдельно.

## Учёт и проверка

Начните запись с шести своих карт, имён игроков и открытого козыря. Идентификаторы `p0`, `p1`, `p2` закреплены за местами игроков до конца партии. Записывайте подтверждённые карты и события через `--cards apply`. При пропущенных событиях отмечайте разрыв учёта. Движок `--cards decide` использует текущую позицию и историю из той же базы.

```powershell
dotnet build/DurakMouse.dll --cards status
.\build\DurakMouse.exe --cards-window
.\build\DurakMouse.exe --cards-statistics
dotnet build/DurakMouse.dll --cards-self-test
dotnet build/DurakMouse.dll --cards-strategy-test
node agent-session.test.cjs
```
