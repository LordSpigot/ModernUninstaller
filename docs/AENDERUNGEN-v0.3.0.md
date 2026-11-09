# Änderungen v0.3.0

## Dialog-Assistent (lokales maschinelles Lernen)
- Ersetzt die feste Button-Liste der „Schnelldeinstallation“.
- Modell: zweistufig. Checkboxen/Optionsfelder per logistischer Regression („jetzt umschalten?“), Schaltflächen per Softmax-Ranking inklusive Option „Warten“. Merkmale: Beschriftung (Wörter, Zeichen-Trigramme), sprachübergreifende Begriffsgruppen, Fenstertext und deren Kreuzungen; Feature-Hashing (2^18 Gewichte).
- Startwissen: 128 Beispiel-Dialoge (NSIS, Inno, InstallShield, MSI, Umfragen, Angebote, Neustart, gemeinsame Dateien) in DE, EN, FR, ES, IT, NL, SV.
- Leave-one-out mit Sicherheitsregeln: 101 von 128 nie gesehenen Dialogen richtig bedient, 23 Rückfragen, 4 falsch (alle bei „Anwendungen automatisch schließen“, harmlos).
- Rückfrage-Dialog bei Unsicherheit; Antworten werden gespeichert und dreifach gewichtet nachtrainiert. „Gelerntes vergessen“ in den Einstellungen.
- Sicherheitsschicht: kein Neustart, kein Ankreuzen, kein Reparieren/Installieren/Kaufen/Senden, Fehlermeldungen und „Nein“ auf eine Deinstallationsfrage gehen an den Benutzer, Schleifenschutz bei unverändertem Fenster.
- Behoben: Der alte Automatismus konnte den „Schließen“-Knopf der Titelleiste anklicken und so die Deinstallation abbrechen. Titelleisten-Knöpfe werden jetzt ignoriert.
- Es wird auf den ganzen Prozessbaum des Uninstallers gewartet (Kopie in %TEMP%), Browser für Umfrageseiten zählen nicht mit.

## Microsoft 365 / Office Click-to-Run
- Sprachpakete werden ohne Office-Fenster entfernt (`DisplayLevel=False`), mehrere gewählte nacheinander in einem Durchgang.
- Warnung, wenn alle Sprachen eines Produkts gewählt sind (Office würde ganz entfernt).
- Kein Reste-Scan, solange noch ein anderes Office-Sprachpaket installiert ist. Allgemein wird ein Installationsordner, den ein anderes installiertes Programm nutzt, nie als Rest angeboten (vorher hätte das Entfernen eines Sprachpakets den ganzen Ordner „Microsoft Office“ als Rest vorgeschlagen).

## Größenanzeige
- Teilen sich mehrere Einträge einen Installationsordner, wird die Größe nur einmal gezählt; die anderen zeigen „geteilt“ (mit Tooltip). Vorher: 16 × 5,8 GB.

## Hinweis
Diese Version wurde ohne Windows-Build erstellt. Bitte `dotnet test` laufen lassen; die neuen Tests stehen in `tests/ModernUninstaller.Tests/DialogAssistantTests.cs`.

## v0.3.1 – „Es passiert nichts“ beim Deinstallieren
- Office Click-to-Run wird jetzt immer mit Administratorfreigabe (UAC) gestartet. Ohne Adminrechte brach der stille Modus lautlos ab und die App wartete bis zu 20 Minuten.
- Office-Fortschritt: erkennt, wenn Office nicht mehr arbeitet (90 s ohne Office-Prozess) und bricht dann mit Meldung und Exit-Code ab, statt weiterzuwarten. Danach wird angeboten, den Office-eigenen Dialog zu öffnen.
- Fenster des Original-Uninstallers werden nach vorne geholt; vorher konnten sie hinter dem Deinstallations-Dialog liegen.
- Statusanzeige mit Laufzeit; zeigt der Uninstaller nach 10 s kein Fenster, gibt es einen Hinweis (UAC-Abfrage in der Taskleiste?).
- Protokoll aller Schritte (Befehl, PID, Exit-Code, Fenster gesehen, Eintrag entfernt): `%LocalAppData%\ModernUninstaller\uninstall.log`.
