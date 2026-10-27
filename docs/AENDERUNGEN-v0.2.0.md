# Modern Uninstaller v0.2.0 – Änderungen

## Oberfläche

Die bisherige Oberfläche bleibt bestehen: Schwarz/Grau mit grünem Akzent, gleiche Navigation, gleiche Karten, gleiche Fensterstruktur und bisherige Dialoge. Nur das bisherige "M"-Symbol links oben wurde durch ein neues Logo ersetzt. Das Logo ist außerdem in der Windows-EXE und in der Titelleiste eingebunden.

## UAC nur einmal beim Software-Check

1. App normal starten, ohne UAC-Abfrage.
2. Im bisherigen **Software-Check** Prüfoptionen auswählen und **Scannen** klicken.
3. Ist die App nicht bereits erhöht, erscheint **einmal** der Windows-UAC-Dialog.
4. Nach der Bestätigung startet sich Modern Uninstaller als Administrator und übernimmt dieselben Prüfoptionen automatisch.
5. Prüfung und spätere **Bereinigen**-Aktion laufen erhöht im selben Prozess. Keine weitere UAC-Freigabe pro Datei erforderlich. Auch wiederholte Software-Checks innerhalb des erhöhten Prozesses verlangen keine UAC-Abfrage.
6. Wird UAC abgebrochen, bleibt die normale App offen. Eine spätere Auswahl ist jederzeit möglich.

**Wichtig:** Das ist eine bewusste Rechteerhöhung für das **gesamte** Software-Check-Fenster (nicht ein Treiber oder Dienst). Windows zeigt bei einer neu gestarteten Instanz wieder eine UAC-Abfrage. Die bestehende manuelle Funktion **Als Administrator neu starten** bleibt erhalten. Bei Windows-Standardkonten, die bei der Freigabe andere Admin-Zugangsdaten verwenden, können sich profilbezogene Pfade auf das Administratorkonto beziehen.

## Performance

- Software-Check: Die drei voneinander unabhängigen Prüfungen laufen parallel (`Task.WhenAll`).
- Temp-Scan: Statt einer Liste aller alten Dateien hält eine `PriorityQueue` nur die **1.000 größten Kandidaten** gleichzeitig im Speicher. Die Dateisuche muss trotzdem alle Einträge im Ordner durchlaufen, damit große Kandidaten nicht übersehen werden.
- Temp-Scan: `LastWriteTimeUtc` statt wiederholtem Zugriff auf letztes Zugriffsdatum (`LastAccessTime`, das unter Windows häufig nicht aktualisiert wird). Gemeint ist seit drei Tagen **unverändert**, nicht seit drei Tagen **unbenutzt**.
- Größen im Programme-Tab: Höchstens zwei gleichzeitige Scanner, einmaliger Scan pro identischem Installationspfad und Cache pro App-Lauf. Bei erneutem Laden wird die vorherige Messung abgebrochen.
- Windows Apps: Keine unnötige komplette rekursive Ordner-Größenermittlung in WindowsApps mehr.

**Wichtiger Hinweis:** Dies sind codebasierte Optimierungen, keine gemessenen Benchmark-Ergebnisse auf dem Ziel-PC.

## Dateien

- `src/ModernUninstaller/Assets/ModernUninstaller.png` – Logo für bestehende Sidebar
- `src/ModernUninstaller/Assets/ModernUninstaller.ico` – Windows-Icon mit mehreren Größen
- `src/ModernUninstaller/MainWindow.xaml`, `ModernUninstaller.csproj` – Logo eingebunden
- `MainWindow.xaml.cs`, `App.xaml.cs`, `Pages/HealthPage.xaml.cs` – einmaliger Admin-Neustart und Fortsetzung
- `Services/HealthService.cs`, `Services/ProgramScanner.cs`, `Pages/ListPage.xaml.cs` – Performance

## Prüfen und bauen

Die Dateien wurden auf XML-/XAML-Wohlgeformtheit und die Integration der Bildressourcen geprüft. Die App benötigt WPF/.NET 9 und muss unter **Windows** kompiliert und getestet werden. In der aktuellen Bearbeitungsumgebung ist kein .NET SDK und kein Windows verfügbar; daher wurde **kein** erfolgreicher Windows-Build oder Laufzeittest behauptet.

```powershell
dotnet test ModernUninstaller.sln -c Release
dotnet publish src/ModernUninstaller/ModernUninstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/win-x64
```

Auch die vorhandene GitHub-Action `.github/workflows/build.yml` testet und veröffentlicht x64/ARM64 auf `windows-latest`.
