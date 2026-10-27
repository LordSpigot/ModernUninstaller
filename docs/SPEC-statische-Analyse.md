# Spezifikation: moderner Uninstaller (Vorbild IObit Uninstaller 16.0.0.34)

Quelle: statische Analyse von `Language\German.lng`, `Skin`, `Database`, Toolbox-Texten.
NICHT durch Bedienung der laufenden GUI verifiziert (Computer Use war nicht verfuegbar).
Layout, Farben und Abstaende sind daher noch unbestaetigt.

## Navigation (MainForm_M)
Linke Navigation, gruppiert:
- Programme: Alle | Bundleware | Kuerzlich installiert | Selten genutzt | Uebergrosse Programme | Install Monitor | Protokolle
- Browser-Erweiterungen
- Windows Apps: Drittanbieter-Apps | Microsoft-Apps | Android Apps
- Action Center (Werbung fuer IObit-Produkte, nicht uebernehmen)
- Software Health
- Toolbox
- Oben: Suche ("Alle installierten Programme durchsuchen"), Hell/Dunkel-Umschalter, Zahnrad-Menue:
  Einstellungen, Wiederherstellung, Programmliste exportieren, Aktualisieren, Benutzerhandbuch,
  Deinstallationsverlauf, Info, Was ist neu

## Programmliste
- Spalten: Bezeichnung, Groesse, Installiert am, Zuletzt genutzt, Version, Herausgeber, Installationspfad, Bewertung, 32/64 bit
- Filter/Sortierung (Filtern nach / Sortieren nach), Option "Zeige System-Laufzeitkomponenten"
  (.NET, VC++ Redistributable, XNA), Laufwerksfilter ("Alle Laufwerke")
- Selten genutzt: Schwelle in Monaten (Einstellung); Uebergross: Schwelle in Groesse
- Kontextmenue: Deinstallation, Reparieren, Aktualisieren, Speicherort oeffnen, In Regedit oeffnen,
  Online suchen, Website oeffnen, Ausfuehren
- Mehrfachauswahl + "Deinstallieren"

## Deinstallations-Ablauf
1. Bestaetigung (n Programme) + Option "Wiederherstellungspunkt erstellen" + "Reste automatisch entfernen"
2. Optional Systemwiederherstellungspunkt (Checkpoint-Computer / SRClient)
3. Original-Uninstaller starten (UninstallString / QuietUninstallString); "Schnelldeinstallation" bestaetigt Dialoge automatisch
4. Laufende Prozesse der App erkennen und beenden (nach Rueckfrage)
5. Scan auf Reste: Dateien, Registry, Geplante Aufgaben, Systemdienste, DLL-Registrierungen
   (+ protokollierte Elemente aus Install Monitor)
6. Ergebnisliste mit Auswahl, Hinweis wenn persoenliche Dateien (Video/Musik/Bilder) markiert
7. Loeschen (optional in Papierkorb), Ergebnisseite mit Zaehlern, Neustart-Hinweis ("Bei Neustart loeschen" via MoveFileEx)
- Bundleware: zusammen installierte Programme gemeinsam deinstallieren

## Toolbox
- Erzwungene Deinstallation (Datei/Ordner ziehen -> zugehoeriges Programm finden)
- Software-Updater (winget als Backend sinnvoll)
- Entferner fuer hartnaeckige Programme (Datenbank offizieller Remover)
- Deinstallationsverlauf
- Datei-Shredder (Modi: Schnell / Erweitert / Fachgerecht; Kontextmenue-Eintrag)
- Einfache Deinstallation (Drag-Ziel-Symbol auf Fenster/Desktopsymbol/Tray)
- Windows-Update-Deinstallation
- Software-Verschieber (Programme auf anderes Laufwerk, mit Verlauf + Rollback)
- Bloatware-Entferner
- Install Monitor (Installationen ueberwachen, Aenderungen protokollieren)

## Windows Apps
- Appx/MSIX ueber Get-AppxPackage / Remove-AppxPackage; Admin noetig; Kontextmenue: Deinstallation, Details,
  Speicherort, Regedit, Microsoft Store, online suchen

## Browser-Erweiterungen
- Chrome/Edge/Firefox Extensions erkennen, entfernen, ignorieren

## Einstellungen
Oberflaeche (Hell/Dunkel, Sprache, Schriftgroesse, Transparenz, System-DPI), Allgemein
(Reste automatisch entfernen, in Papierkorb, Wiederherstellungspunkt, Kontextmenue-Eintraege,
Hotkey Schnelldeinstallation, UAC ueberspringen, Monitor mit Windows starten), Benachrichtigungen,
Ignorierlisten, Installationsdateien bereinigen.

## Bewusst NICHT uebernehmen
Action Center / Produktwerbung, Pro-Upsell, Lizenzverwaltung, Telemetrie (ProductStat, Feedback, Umfragen).

## Hinweis Architektur
- ARM64 + x64: .NET 8/9 (WinUI 3 oder WPF) self-contained mit RID win-x64 und win-arm64.
  Das Original ist Delphi (x86, rtl120.bpl) und hat keinen ARM64-Build.
- Installations-Quellen: HKLM/HKCU Uninstall inkl. WOW6432Node, Appx, winget.
