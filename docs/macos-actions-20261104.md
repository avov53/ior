# GitHub Actions для macOS

GitHub CLI установлен и авторизован. PR: https://github.com/avov53/ior/pull/1. Ветка codex/macos-actions-20261104; main не изменён.

Двойной щелчок dist.bat собирает Windows локально. При доступном GitHub проверяет точные входные файлы macOS по .local/macos-remote.json и запускает или находит соответствующую удалённую сборку. Скачиваются ZIP, README, build.json и SHA256SUMS.txt. Проверяются коммит, архитектура и SHA256, затем файлы переносятся в dist/macos-arm64 и dist/macos-x64. Файлы Mac следует пересылать внутри ZIP.

Если workflow ещё отсутствует в main, сценарий может использовать сборку от push в согласованную ветку. После слияния PR доступен обычный workflow_dispatch. При изменении входных файлов нужно отправить обновлённые исходники; dist.bat не отправляет изменения автоматически. При отсутствии GitHub или ошибке Mac сборка Windows продолжает работать. Строгий режим: dist.bat -RequireMacOS. Только Windows: dist.bat -WindowsOnly.

Для повторного скачивания: powershell -File scripts/Build-MacOSRemote.ps1 -RunId NUMBER.

Рабочие Windows EXE сохранены и сверены по SHA256 с .local/backups/macos-actions-20261104/windows-hashes.json. Сценарии сохранены в той же резервной папке. Полный перенос VPN на macOS не завершён: пакеты пока содержат предварительную оболочку, а не рабочий Mac VPN.

## Подтверждённый результат

Полный dist.bat -RequireMacOS завершился с кодом 0. Успешный запуск: https://github.com/avov53/ior/actions/runs/41845385950. Созданы Windows 10/11 и скачаны оба Mac ZIP. Контрольные суммы ZIP, наличие ядра и Mach-O архитектуры проверены; SHA256 Windows EXE совпадают с сохранёнными до изменений. Подпись проверена codesign в CI. Физический Mac не проверялся; VPN остаётся недоступен в предварительной оболочке. Последний коммит PR содержит только прошедшие локальный тест исправления сценариев PowerShell; отдельная пересборка неизменных Mac-входов для него пропущена. Финальная preflight-проверка проходит.
