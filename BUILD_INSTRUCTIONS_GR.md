# KYMA RADIO AUTOMATION — Αυτόματο Windows build

Το GitHub Actions workflow `.github/workflows/windows-build.yml` δημιουργεί Windows x64 self-contained έκδοση .NET 8 WPF και ανεβάζει το αποτέλεσμα ως downloadable artifact.

## Απαραίτητα αρχεία

Στο repository πρέπει να υπάρχει ο φάκελος `src/` από το KYMA SOHO Classic v1.3, με τα αρχεία `KymaSoho.csproj`, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`.

## Εκτέλεση

GitHub → Actions → KYMA Windows x64 Build → Run workflow. Αν πετύχει, κατεβάζουμε το artifact `KYMA-RADIO-AUTOMATION-Windows-x64` από το ίδιο run.

Το build δεν έχει ακόμη εκτελεστεί και το repository δεν περιέχει ακόμη τον πηγαίο κώδικα. Το self-contained publish δεν αποτελεί ψηφιακή υπογραφή. Το Smart App Control ενδέχεται να μπλοκάρει ανυπόγραφο EXE.
