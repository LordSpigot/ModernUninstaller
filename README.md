# Modern Uninstaller

Ein moderner Uninstaller für Windows mit Dunkel- und Hellmodus, der Programme **restlos** entfernen soll. Die App läuft nativ unter **x64** und **ARM64** (WPF, .NET 9, self-contained, eine einzelne EXE).

> Unabhängiges Projekt. Es ist nicht mit IObit verbunden und enthält weder deren Code noch deren Grafiken oder Texte. Die Oberfläche orientiert sich lediglich an der Bedienlogik üblicher Uninstaller; die Notizen dazu stehen in [`docs/`](docs).

## Funktionen

**Deinstallieren, ohne Reste**
1. Bestätigung mit Optionen (Wiederherstellungspunkt, Reste automatisch entfernen)
2. Laufende Prozesse des Programms werden (nach Rückfrage) beendet
3. Original-Uninstaller läuft (MSI wird automatisch von `/I` auf `/X` umgestellt); optional bestätigt die „Schnelldeinstallation“ dessen Rückfragen
4. Reste-Scan, Ergebnis als Auswahlliste
5. Löschen: Papierkorb oder endgültig; gesperrte Dateien werden für den nächsten Neustart vorgemerkt

**Der Reste-Scan findet**

| Bereich | Was |
| --- | --- |
| Dateien | Installationsordner, Ordner in Program Files, ProgramData, AppData (Roaming/Local/LocalLow), Herausgeberordner, danach leer gewordene Elternordner |
| Verknüpfungen | Startmenü, Desktop, Autostart, Taskleiste, per Ziel (nicht nur per Name) |
| Registry | `Software\<Name>` (64/32 bit, HKCU/HKLM), Herausgeber-Schlüssel, Autostart-Werte inkl. StartupApproved, App Paths |
| Zuordnungen | ProgIDs, Dateizuordnungen und Shell-Verben, deren Befehle in den Installationsordner zeigen |
| System | Dienste und Treiber, geplante Aufgaben, COM-/DLL-Registrierungen, Typbibliotheken, Firewall-Regeln, PATH-Einträge |
| Persönliches | Ordner in „Dokumente“ werden angezeigt, aber nie vorausgewählt |

Zusätzlich gibt es den **Installations-Monitor** (Toolbox): Er vergleicht den Rechner vor und nach einer Installation und speichert, was neu dazukam. Beim Deinstallieren dieses Programms werden diese Elemente ebenfalls als Reste angeboten (Tab „Protokolle“).

**Weiteres**: Windows Apps (Appx), Browser-Erweiterungen (Chrome, Edge, Brave, Firefox), Software-Check, Toolbox mit Datei-Shredder, Erzwungener Deinstallation, Windows-Update-Deinstallation, Bloatware-Entferner, Software-Updater (winget) und Deinstallationsverlauf.

## Sicherheit beim Löschen

- Nichts wird gelöscht, bevor die Liste bestätigt wurde (außer bei „Reste automatisch entfernen“).
- Systemordner, Benutzerordner, Program Files selbst, Startmenü- und Desktop-Wurzeln sind geschützt.
- Zu kurze oder allgemeine Namen („Microsoft“, „Common Files“ …) werden nie als Suchbegriff benutzt.
- Dateien gehen standardmäßig in den Papierkorb.

Der Scan ist eine Heuristik. Prüfen Sie die Liste, besonders bei Programmen mit sehr allgemeinen Namen.

## Bauen

Voraussetzung: [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) unter Windows.

```powershell
dotnet test ModernUninstaller.sln -c Release

dotnet publish src/ModernUninstaller/ModernUninstaller.csproj -c Release -r win-arm64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/win-arm64
dotnet publish src/ModernUninstaller/ModernUninstaller.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/win-x64
```

Die GitHub-Action [`build.yml`](.github/workflows/build.yml) führt die Tests aus und baut beide Varianten. Bei einem Tag `v*` hängt sie die EXEs an ein Release.

## Hinweise

- **Administratorrechte (v0.2)**: Beim **Start des Software-Checks** fragt Windows einmal nach UAC-Freigabe, falls die App noch nicht als Administrator läuft. Die App startet sich erhöht neu und übernimmt die ausgewählten Prüfungen automatisch. Scan und anschließende Bereinigung laufen dann im **selben** Adminprozess, ohne weitere Freigabe pro Datei. Bei abgebrochener UAC-Abfrage bleibt die bisherige App offen. Ein bloßes Öffnen der App benötigt keine Adminrechte. Die vorhandene Option „Als Administrator neu starten“ bleibt bestehen.
- **Performance (v0.2)**: Der Software-Check führt unabhängige Teilprüfungen parallel aus. Der Temp-Scan merkt sich maximal die 1.000 größten Kandidaten im Speicher statt sämtliche Treffer. Die Hintergrund-Größenermittlung scannt jeden Installationsordner höchstens einmal pro App-Lauf und verwendet maximal zwei gleichzeitige I/O-Worker; unnötige Windows-App-Größenscans entfallen.
- **Logo (v0.2)**: Neues grün-graues `M`-Icon passend zum ursprünglichen Schwarz/Grau/Grün-Design. Integration in Titelleiste, EXE und bisherige Sidebar ohne Layout-Änderung.
- Daten (Einstellungen, Verlauf, Monitor-Protokolle) liegen unter `%LocalAppData%\ModernUninstaller`.
- Nur Deutsch.

## Stand

Noch nicht umgesetzt: Software-Verschieber, Entferner für hartnäckige Programme, weitere Sprachen, Rechtsklick-Menü der Programmliste, automatischer Monitor beim Windows-Start.

## Lizenz

Noch nicht festgelegt. Vor dem Veröffentlichen eine `LICENSE`-Datei ergänzen.
