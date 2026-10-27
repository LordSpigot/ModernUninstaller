# Verifizierte UI-Beobachtungen (IObit Uninstaller 16.0.0.34, per Computer Use, Dunkelmodus)

Ergaenzt SPEC-IObitUninstaller16.md. Alles hier wurde am laufenden Programm gesehen.
Nicht getestet: Rechtsklick-Kontextmenue (Windows blockiert simulierte Rechtsklicks, UIPI), echter
Deinstallationslauf, Hellmodus, Toolbox-Unterfenster.

## Voraussetzung
- Auf ARM64-Windows startet das x86-Original nur mit installierter VC++ 2015-2022 Redistributable (x86).

## Fenster
- Rahmenloses Fenster, ca. 1225 x 760 px, stark abgerundete Ecken (~20 px), fast schwarzer Hintergrund (#0d0d0d),
  Inhaltsflaeche eine Stufe heller (#1c1c1c) mit abgerundeten Ecken (~16 px).
- Akzentfarbe Gruen (~#3ddc7a). Aktive Elemente: gruene Kontur/Text, Hover-Tooltip neben dem Icon.
- Titelzeile: Logo links, mittig Suchfeld (Pillenform, "Alle installierten Programme durchsuchen..."),
  rechts Designumschalter, Hamburger-Menue, Minimieren, Maximieren, Schliessen.
- Linke Icon-Leiste (nur Icons, Tooltip beim Hover), aktive Seite: gruner Balken links + gruene Kachel.
  Reihenfolge: Programme, Software Health, Browser-Extensions, Windows Apps, Toolbox, Action Center.
  Unten: PRO-Button (orange Kontur) -> nicht uebernehmen.
- Seitentitel gross (Programme / Software-Check / Browser-Extensions / Windows Apps / Toolbox / Action Center).

## Programme
- Register (Pillen): Alle | Bundleware | Protokolle | Uebergrosse Programme | Selten genutzt (aktiv = gruen umrandet).
- Zeile darunter: Dropdown "Filtern nach: Alle Laufwerke" (Menue: [x] Zeige System-Laufzeitkomponenten, Laufwerke Alle / C:\)
  und "Sortieren nach: Bezeichnung" (Bezeichnung | Groesse | Installationsdatum).
- Liste: Karten-Zeilen mit Checkbox, Icon, Name, Groesse, Installationsdatum, gruenes Papierkorb-Icon (Einzel-Deinstallation).
  Ausgewaehlte Zeile: gruener Rahmen.
- Rechtes Panel ohne Auswahl: grosses Icon, "N Programme gefunden", Karte "Kuerzlich installiert" (3 Eintraege + "Heute").
- Rechtes Panel mit Auswahl: Icon, Name, Groesse, Version, Herausgeber, Installationspfad, Installationsdatum.
- Unten rechts gruener Button "Deinstallieren" (Mehrfachauswahl per Checkbox).
- Protokolle leer: Lupen-Icon + "Programme, die mit 'Install Monitor' ueberwacht wurden, koennen spaeter gruendlich deinstalliert werden."
- Bestaetigungsdialog (modal, Rest abgedunkelt/unscharf): Icon, Name, Groesse, "Das gewaehlte Programm deinstallieren?",
  Checkboxen "Vor Entfernen Wiederherstellungspunkt erstellen" und "Dateireste automatisch entfernen",
  Buttons Abbrechen (grau) / Deinstallieren (gruen).

## Software Health ("Software-Check")
- Checkliste (alle vorab angehakt): Reste von Deinstallationen, Veraltete Software, Software Redundante Dateien,
  Software-Ruhezustand, Software-Berechtigungen, Stoerende Benachrichtigungen, Programme mit Deinstallationsproblemen,
  Installationsdateien. Jeweils Titel + Einzeiler.
- Rechts: "Nie gescannt", Karte "Jetzt scannen", Button "Scannen".

## Browser-Extensions
- Register: Alle | Microsoft Edge (je gefundener Browser). Sortieren nach: Bezeichnung.
- Gruppe "Vertrauenswuerdige Toolbars und Plug-ins" (einklappbar), Zeile mit Checkbox, Icon, Name, Sterne-Bewertung, Papierkorb.
- Rechts: "N Erweiterungen gefunden", Karte "Erweiterungs-Check", Button "Entfernen".

## Windows Apps
- Register: Alle | Microsoft-Apps | Drittanbieter-Apps. Zeilen wie Programme (mit Groesse und Sterne-Bewertung).
- Rechts: "N Apps gefunden", "Kuerzlich installiert". Button "Deinstallieren".

## Toolbox (Kachelraster 5 Spalten)
Zeile 1: Installations-Monitor, Software-Updater, Einfache Deinstallation (Strg+Alt+U), Erzwungene Deinstallation+,
Entferner fuer hartnaeckige Programme. Zeile 2: Datei-Shredder, Software-Verschieber, Bloatware-Entferner,
Windows-Update-Deinstallation. Kachel: grosses Linien-Icon + Beschriftung, abgerundet, dunkelgrau.

## Action Center
- Nur Werbung fuer IObit-Produkte mit "Installieren"-Buttons. Nicht uebernehmen.

## Hamburger-Menue (Seitenpanel rechts, Icons + Text)
Einstellungen..., Deinstallationsverlauf, Wiederherstellung, Exportiere Progr.-liste | Benutzerhandbuch,
Technische Hilfe, Feedback, Hilfe beim Uebersetzen | Was Ist Neu, Aktualisieren, Info, Meine Lizenz.

## Einstellungen (modal, links Kategorien, rechts scrollbarer Inhalt, unten Abbrechen / Okay)
- Bedienoberflaeche: Sprache (Dropdown), Aussehen (zwei Vorschaukarten dunkel/hell), Schriftgroesse (3x "Aa"), Transparenz (Slider).
- Allgemein: UAC ueberspringen, System-DPI, Monitor mit Windows starten, Zeige System-Laufzeitkomponenten,
  "Selten genutzt" seit (2 Monate), "Uebergross" ab (500 MB), Hotkey Schnelle Deinstallation (STRG+ALT+U).
- Deinstallation: Wiederherstellungspunkt vorher, Schnelldeinstallation (Standard an), Dateireste automatisch entfernen,
  Dateireste in Papierkorb, "Gruendliche Deinstallation" im Kontextmenue.
- Software Health: Auto-Update (PRO), Download-Ordner, zu pruefende Berechtigungen (Starteintraege, Geplante Aufgaben, Dienste, Desktopsymbole).
- Benachrichtigungen: Bundleware, schaedliche Plug-ins, Updates, Restdateien, Speicher unter 10 %, Protokoll nach Auto-Monitor,
  Pop-ups, unnoetige Installationsdateien.
- Ignorierliste: Bundleware-Ignorierliste, Software-Health-Ignorierliste (je mit "Entfernen").
