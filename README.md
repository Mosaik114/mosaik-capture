# Mosaiks Aufnahme

Eigenständige Windows-App für Screenshots und Bildschirmaufnahmen, ohne Aktionsring oder Mausrad-Belegung.

| Tastenkürzel | Funktion |
|---|---|
| **Umschalt + Alt + 4** | Screenshot-Bereich auswählen |
| **Umschalt + Alt + 5** | Aufnahme-Bereich auswählen; erneut drücken beendet die Aufnahme |
| **Esc** | Bereichsauswahl oder Vorlauf abbrechen |

In **Mosaiks Suche** öffnen **Mosaiks Screenshot** und **Mosaiks Bildschirmaufnahme** die passenden Einstellungen. Dort lassen sich Speicherordner, Kürzel, Mikrofon, Systemton, Klickanzeige, Bildrate, Vorlauf und Windows-Autostart ändern. Ein belegtes Kürzel wird abgewiesen; die bisherigen Kürzel bleiben aktiv. Die App ist auch über ihr Symbol im Infobereich erreichbar.

## Screenshot

Bereich aufziehen und loslassen, ein Fenster anklicken oder mit der Leertaste den ganzen Monitor aufnehmen. Das Bild wird als PNG gespeichert und in die Zwischenablage kopiert. Die anschließende Karte bietet den vorhandenen Editor, Anpinnen und Texterkennung.

Standardordner: **Bilder\Mosaiks-Screenshots**.

## Bildschirmaufnahme

Nach der Bereichsauswahl beginnt der eingestellte Vorlauf. Die Aufnahmeleiste bietet Pause, Stopp sowie Mikrofon, Systemton und Klickanzeige. Dasselbe Tastenkürzel beendet die Aufnahme. Danach stehen die vorhandenen Funktionen zum Kürzen und GIF-Export zur Verfügung.

Standardordner: **Videos\Mosaiks-Aufnahmen**. Das Mikrofon ist zunächst ausgeschaltet, der Systemton eingeschaltet.

## Aufbau und Bauen

Die neue App-Steuerung, native Einstellungsoberfläche, globale Tastenkürzel, Einzelinstanz und Konfiguration sind in **Rust** geschrieben (`src`). Der vorhandene Windows-Editor und Aufnahmekern bleiben als getrennte **C#-/WPF-Komponente** erhalten (`engine/src`). Suche, Aktionsring und die sonstigen Desktop-Einblendungen sind nicht Bestandteil dieser App.

Es werden keine Rust-Crates heruntergeladen. Der Offline-Build verwendet den vorhandenen Rust-Compiler/LLVM-Linker, das Windows SDK und den C#-Compiler des .NET Framework. Vor einem Neubau die App über ihr Infobereich-Menü beenden; laufende Aufnahmen zuerst speichern.

```powershell
python build.py --test
```

Die Programme entstehen in `bin`. Zum Betrieb sind Rust und Python nicht erforderlich. Verwendet werden Windows 10/11 x64, die vorhandene VC-Laufzeit, .NET Framework/WPF und für Videos das installierte ffmpeg/ffprobe.

`prepare_engine.py` aktualisiert bei Bedarf die Kopie der ursprünglichen Capture-Quellen und wendet die Trennung erneut an. Der normale Build arbeitet mit den mitgelieferten Quellen und verändert die ursprüngliche Mosaiks-Suche nicht.

## Lokale Daten

- Einstellungen: `%LOCALAPPDATA%\Mosaiks-Aufnahme\settings.ini` (Unicode).
- Temporäre Videoteile: `%LOCALAPPDATA%\Mosaiks-Aufnahme\Aufnahme`.
- Capture-Fehlerprotokoll: `bin\Fehler.log`.
- Der Windows-Autostart verwendet die feste Rust-Programmdatei mit `--background`.

Die Prüfungen umfassen Kürzelvalidierung, Konfigurationswerte, PNG-Bilddaten, Videoabmessungen, sichere ffmpeg-Pfade sowie einen Live-Test mit einem eigenen Testbild. `qa.py` enthält die ergänzenden Terminal-Prüfungen für Einstellungen, Kürzelkonflikte und die Auswahlfenster.
