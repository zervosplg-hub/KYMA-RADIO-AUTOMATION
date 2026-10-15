using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Text;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace KymaSoho;
public sealed class Track {
    public string Path { get; set; } = "";
    public string Display => System.IO.Path.GetFileNameWithoutExtension(Path);
    private string? searchCache;
    public string SearchText => searchCache ??= (Display+" "+Path).ToUpperInvariant();
}
public sealed class FastTrackCollection : ObservableCollection<Track> {
    public void ReplaceAll(IEnumerable<Track> items) {
        Items.Clear();
        foreach(var item in items)Items.Add(item);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
public partial class MainWindow : Window
{
    private readonly Stopwatch sessionClock = Stopwatch.StartNew();
    private readonly Process diagnosticsProcess=Process.GetCurrentProcess();
    private TimeSpan lastCpuTime=TimeSpan.Zero;
    private DateTime lastCpuSample=DateTime.UtcNow;
    private DateTime lastPerformanceLog=DateTime.MinValue;
    private CancellationTokenSource? cleanCancellation;
    private CancellationTokenSource? importCancellation;
    private readonly string errorLogPath=System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KYMA_SOHO","diagnostics-errors.log");
    private void CancelBackgroundJobs(){
        cleanCancellation?.Cancel();importCancellation?.Cancel();
        LogDiagnostic("JOB","Cancellation requested for CLEAN / IMPORT");
    }
    private void PersistDiagnosticError(string message){
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(errorLogPath)!);
            File.AppendAllText(errorLogPath,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")+" "+message+Environment.NewLine);
        }catch { }
    }
    private readonly Stopwatch startupClock = Stopwatch.StartNew();
    private readonly List<string> diagnosticLines = new();
    private readonly DispatcherTimer responsivenessTimer = new() { Interval=TimeSpan.FromSeconds(1) };
    private DateTime lastHeartbeat = DateTime.UtcNow;
    private Window? diagnosticsWindow;
    private TextBox? diagnosticsText;
    private int completedCrossfades=0;
    private DateTime crossfadeStartedUtc=DateTime.MinValue;
    private string crossfadeFrom="";
    private string crossfadeTo="";
    private long crossfadeStartRamMb=0;
    private long baselineMemoryMb=0;
    private long peakMemoryMb=0;
    private long lastMemoryMb=0;
    private int stableMemorySamples=0;
    private long MemoryMb(){
        diagnosticsProcess.Refresh();
        return diagnosticsProcess.WorkingSet64/1048576;
    }
    private void RecordMemorySample(){
        long mb=MemoryMb();
        if(baselineMemoryMb==0)baselineMemoryMb=mb;
        peakMemoryMb=Math.Max(peakMemoryMb,mb);
        if(lastMemoryMb>0 && Math.Abs(mb-lastMemoryMb)<=8)stableMemorySamples++;
        else stableMemorySamples=0;
        lastMemoryMb=mb;
    }
    private int consecutiveAudioFailures=0;
    private string? lastStandbyFailedPath;
    private int standbyRetryCount=0;
    private readonly Dictionary<MediaPlayer,(DateTime started,string path)> openRequests = new();
    private void TrackMediaOpen(MediaPlayer target,string path,string role) {
        openRequests[target]=(DateTime.UtcNow,path);
        LogDiagnostic("AUDIO",$"{role} OPEN requested: {System.IO.Path.GetFileName(path)}");
    }
    private void LogDiagnostic(string category,string message) {
        var line=$"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";
        diagnosticLines.Add(line);
        if(category=="ERROR")PersistDiagnosticError(line);
        if(diagnosticLines.Count>1500)diagnosticLines.RemoveRange(0,diagnosticLines.Count-1500);
        if(diagnosticsText!=null) {
            diagnosticsText.AppendText(line+Environment.NewLine);
            diagnosticsText.ScrollToEnd();
        }
    }
    // KYMA PRO: first scheduling module. Preview is read-only and cannot alter ON AIR.
    private void OpenClockPreview() {
        var w=new Window {
            Title="KYMA PRO | ΩΡΙΑΙΟ CLOCK / ΠΡΟΕΠΙΣΚΟΠΗΣΗ",
            Width=850,Height=620,MinWidth=620,MinHeight=400,
            Background=new SolidColorBrush(Color.FromRgb(17,25,36)),
            Foreground=Brushes.White,Owner=this,
            WindowStartupLocation=WindowStartupLocation.CenterOwner
        };
        var root=new DockPanel { Margin=new Thickness(16) };
        var heading=new TextBlock {
            Text="ΩΡΙΑΙΟΣ ΠΡΟΓΡΑΜΜΑΤΙΣΜΟΣ · ΔΟΚΙΜΑΣΤΙΚΗ ΠΡΟΕΠΙΣΚΟΠΗΣΗ",
            FontSize=17,FontWeight=FontWeights.Bold,Foreground=Brushes.White,
            Margin=new Thickness(0,0,0,10)
        };
        DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var explanation=new TextBlock {
            Text="Δημιουργεί ενδεικτικό ωριαίο log από την τρέχουσα playlist. Δεν αλλάζει την εκπομπή. Οι διάρκειες είναι εκτιμήσεις μέχρι να υπάρχουν πλήρη metadata.",
            TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(173,195,215)),
            Margin=new Thickness(0,0,0,12)
        };
        DockPanel.SetDock(explanation,Dock.Top);root.Children.Add(explanation);
        var actions=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,10) };
        var generate=new Button { Content="ΔΗΜΙΟΥΡΓΙΑ CLOCK",Padding=new Thickness(14,8,14,8) };
        var copy=new Button { Content="ΑΝΤΙΓΡΑΦΗ LOG",Padding=new Thickness(14,8,14,8) };
        actions.Children.Add(generate);actions.Children.Add(copy);
        DockPanel.SetDock(actions,Dock.Top);root.Children.Add(actions);
        var output=new TextBox {
            IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            FontFamily=new FontFamily("Consolas"),FontSize=13
        };
        root.Children.Add(output);w.Content=root;
        generate.Click+=async (_,_)=>{
            if(queue.Count==0){output.Text="Η playlist είναι κενή.";return;}
            generate.IsEnabled=false;output.Text="Δημιουργία προεπισκόπησης...";
            // Snapshot on UI thread; file metadata and selection stay off the dispatcher.
            var snapshot=queue.Select(x=>(x.Path,x.Display)).ToArray();
            var durations=new Dictionary<string,double>(decodedDurations,StringComparer.OrdinalIgnoreCase);
            DateTime hour=DateTime.Now.Date.AddHours(DateTime.Now.Hour+1);
            try {
                string report=await Task.Run(()=>{
                    var sb=new StringBuilder();
                    sb.AppendLine("KYMA PRO · CLOCK PREVIEW · "+hour.ToString("dd/MM/yyyy HH:mm"));
                    sb.AppendLine("----------------------------------------------------------");
                    double elapsed=0;int selected=0;int missing=0;
                    var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for(int i=0;i<snapshot.Length&&elapsed<3600;i++){
                        var item=snapshot[i];
                        if(!File.Exists(item.Path)){missing++;continue;}
                        if(!used.Add(item.Path))continue;
                        double seconds=durations.TryGetValue(item.Path,out var d)&&d>0?d:180;
                        seconds=Math.Clamp(seconds,1,3600);
                        if(elapsed+seconds>3600 && selected>0)break;
                        sb.AppendLine(hour.AddSeconds(elapsed).ToString("HH:mm:ss")+"  "+
                            item.Display+"  ["+TimeSpan.FromSeconds(seconds).ToString(@"mm\\:ss")+"]"+
                            (durations.ContainsKey(item.Path)?"":" (εκτίμηση)"));
                        elapsed+=seconds;selected++;
                    }
                    sb.AppendLine("----------------------------------------------------------");
                    sb.AppendLine("Στοιχεία: "+selected+" · διάρκεια "+TimeSpan.FromSeconds(elapsed).ToString(@"hh\\:mm\\:ss")+
                        " · κενός χρόνος "+TimeSpan.FromSeconds(Math.Max(0,3600-elapsed)).ToString(@"mm\\:ss")+
                        " · μη διαθέσιμα αρχεία "+missing);
                    sb.AppendLine("PREVIEW ONLY — Δεν έχει δημιουργηθεί εκτελέσιμο πρόγραμμα.");
                    return sb.ToString();
                });
                output.Text=report;
                LogDiagnostic("SCHEDULER","Hourly clock preview generated (no on-air changes)");
            }catch(Exception ex){output.Text="Σφάλμα προεπισκόπησης: "+ex.Message;LogDiagnostic("ERROR","Clock preview: "+ex);}
            finally{generate.IsEnabled=true;}
        };
        copy.Click+=(_,_)=>{if(!string.IsNullOrEmpty(output.Text))Clipboard.SetText(output.Text);};
        w.Show();
    }

    // PRO Operations: independent management tools, isolated from the on-air engine.
    private readonly string proDataDirectory=System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KYMA_SOHO","PRO");

    // KYMA PRO offline scheduling: generates a real plan, never changes live playout.
    private void OpenProScheduler(){
        var w=new Window { Title="KYMA PRO | MUSIC SCHEDULER / FINAL LOG",
            Width=1030,Height=700,MinWidth=700,MinHeight=470,Owner=this,
            Background=new SolidColorBrush(Color.FromRgb(17,25,36)),
            Foreground=Brushes.White,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var root=new DockPanel { Margin=new Thickness(14) };
        var title=new TextBlock { Text="MUSIC SCHEDULER  ·  OFFLINE FINAL LOG",
            FontWeight=FontWeights.Bold,FontSize=19,Margin=new Thickness(0,0,0,8) };
        DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);
        var notice=new TextBlock { Text="Προγραμματισμός από τις αποθηκευμένες κατηγορίες, clocks και rotation rules. Δεν μεταβάλλει το ON AIR. Η αυτόματη εκτέλεση δεν είναι ενεργή.",
            TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10),
            Foreground=new SolidColorBrush(Color.FromRgb(164,196,220)) };
        DockPanel.SetDock(notice,Dock.Top);root.Children.Add(notice);
        var controls=new WrapPanel { Margin=new Thickness(0,0,0,10) };
        var startLabel=new TextBlock { Text="Ώρα:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0) };
        var startHour=new ComboBox { Width=85,Margin=new Thickness(0,0,12,0) };
        for(int i=0;i<24;i++)startHour.Items.Add(i.ToString("00")+":00");
        startHour.SelectedIndex=DateTime.Now.Hour;
        var hoursLabel=new TextBlock { Text="Ώρες:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,5,0) };
        var countHours=new ComboBox { Width=65,Margin=new Thickness(0,0,12,0) };
        foreach(int n in new[]{1,2,4,6,12,24})countHours.Items.Add(n.ToString());
        countHours.SelectedIndex=0;
        var generate=new Button { Content="GENERATE FINAL LOG",Padding=new Thickness(12,8,12,8) };
        var export=new Button { Content="EXPORT CSV",Padding=new Thickness(12,8,12,8) };
        var apply=new Button { Content="APPEND TO PLAYLIST",Padding=new Thickness(12,8,12,8) };
        var saveLog=new Button { Content="SAVE FINAL LOG",Padding=new Thickness(12,8,12,8) };
        controls.Children.Add(startLabel);controls.Children.Add(startHour);
        controls.Children.Add(hoursLabel);controls.Children.Add(countHours);
        controls.Children.Add(generate);controls.Children.Add(export);controls.Children.Add(saveLog);controls.Children.Add(apply);
        DockPanel.SetDock(controls,Dock.Top);root.Children.Add(controls);
        var output=new TextBox { IsReadOnly=true,AcceptsReturn=true,
            FontFamily=new FontFamily("Consolas"),FontSize=12,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto };
        root.Children.Add(output);w.Content=root;
        var generated=new List<(DateTime time,string path,string category,double duration)>();
        string csv="";
        string logText="";
        generate.Click+=async (_,_)=>{
            generate.IsEnabled=false;apply.IsEnabled=false;
            output.Text="Δημιουργία προγράμματος...";
            generated.Clear();csv="";logText="";
            var snapshot=library.Select(t=>t.Path).ToArray();
            var durations=new Dictionary<string,double>(decodedDurations,StringComparer.OrdinalIgnoreCase);
            int hour=startHour.SelectedIndex;
            int number=int.Parse((string)countHours.SelectedItem);
            DateTime beginning=DateTime.Today.AddHours(hour);
            string proPath=proDataDirectory;
            try {
                var result=await Task.Run(()=>{
                    string Read(string name,string fallback){
                        string p=System.IO.Path.Combine(proPath,name+".txt");
                        return File.Exists(p)?File.ReadAllText(p):fallback;
                    }
                    var categoryLines=Read("CATEGORIES","GOLD\nCURRENT\nGREEK\nINTERNATIONAL\nJINGLES").Split('\n')
                        .Select(x=>x.Trim()).Where(x=>x.Length>0&&!x.StartsWith("#")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var rules=Read("ROTATION","ARTIST_SEPARATION_MIN=90\nTITLE_SEPARATION_MIN=240");
                    int artistGap=90,titleGap=240;
                    foreach(var line in rules.Split('\n')){
                        var parts=line.Trim().Split('=',2);
                        if(parts.Length!=2||!int.TryParse(parts[1].Trim(),out var minutes))continue;
                        if(parts[0].Trim().Equals("ARTIST_SEPARATION_MIN",StringComparison.OrdinalIgnoreCase))artistGap=Math.Clamp(minutes,0,1440);
                        if(parts[0].Trim().Equals("TITLE_SEPARATION_MIN",StringComparison.OrdinalIgnoreCase))titleGap=Math.Clamp(minutes,0,1440);
                    }
                    // Category mapping is explicit: "CATEGORY|substring" per line, or ALL by default.
                    var mapLines=Read("CATEGORY_MAP","# CATEGORY|filename substring\n");
                    var mappings=new List<(string category,string token)>();
                    foreach(var line in mapLines.Split('\n')){
                        var parts=line.Trim().Split('|',2);
                        if(parts.Length==2&&parts[0].Length>0&&parts[1].Length>0)
                            mappings.Add((parts[0].Trim(),parts[1].Trim()));
                    }
                    var clockText=Read("CLOCKS","00:00 MUSIC\n00:15 JINGLE\n00:30 MUSIC\n00:45 STATION ID");
                    var slots=new List<(int minute,string category)>();
                    int duplicateClockSlots=0;
                    var invalidClockLines=new List<string>();
                    foreach(var line in clockText.Split('\n')){
                        var parts=line.Trim().Split(' ',2,StringSplitOptions.RemoveEmptyEntries);
                        if(parts.Length==0||parts[0].StartsWith("#"))continue;
                        if(parts.Length!=2||!TimeSpan.TryParse(parts[0],out var slot)){invalidClockLines.Add(line.Trim());continue;}
                        if(slot.TotalMinutes>=60||slot.TotalMinutes<0){invalidClockLines.Add(line.Trim());continue;}
                        slots.Add(((int)slot.TotalMinutes,parts[1].Trim().ToUpperInvariant()));
                    }
                    if(slots.Count==0)slots.Add((0,"MUSIC"));
                    duplicateClockSlots=slots.Count-slots.Select(x=>x.minute).Distinct().Count();
                    slots=slots.OrderBy(x=>x.minute).GroupBy(x=>x.minute).Select(g=>g.First()).ToList();
                    var candidates=new List<(string path,string artist,string category,double duration)>();
                    int missing=0,unmapped=0;
                    foreach(string path in snapshot){
                        if(!File.Exists(path)){missing++;continue;}
                        string name=System.IO.Path.GetFileNameWithoutExtension(path);
                        string artist=name.Split(new[]{" - "," – "},StringSplitOptions.None)[0].Trim();
                        string category=mappings.FirstOrDefault(m=>name.Contains(m.token,StringComparison.OrdinalIgnoreCase)||path.Contains(m.token,StringComparison.OrdinalIgnoreCase)).category??"MUSIC";
                        if(category=="MUSIC"&&mappings.Count>0)unmapped++;
                        double duration=durations.TryGetValue(path,out var known)&&known>0?known:180;
                        candidates.Add((path,artist,category,Math.Clamp(duration,1,3600)));
                    }
                    var entries=new List<(DateTime time,string path,string category,double duration)>();
                    var lastTitle=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
                    var lastArtist=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
                    var sb=new StringBuilder();
                    sb.AppendLine("KYMA PRO FINAL LOG | "+beginning.ToString("dd/MM/yyyy HH:mm")+" | "+number+"h");
                    sb.AppendLine("--------------------------------------------------------------");
                    int cursor=0,violations=0;
                    int fallbackCount=0,estimatedCount=0;
                    for(int h=0;h<number;h++){
                        var hourStart=beginning.AddHours(h);
                        foreach(var slot in slots){
                            DateTime planned=hourStart.AddMinutes(slot.minute);
                            string desired=slot.category;
                            bool matches((string path,string artist,string category,double duration) c)=>
                                (desired=="MUSIC" && !c.category.Equals("JINGLES",StringComparison.OrdinalIgnoreCase) && !c.category.Equals("SPOTS",StringComparison.OrdinalIgnoreCase))||desired=="ALL"||
                                (desired=="JINGLE"&&c.category=="JINGLES")||
                                string.Equals(c.category,desired,StringComparison.OrdinalIgnoreCase);
                            int selected=-1;
                            // A fallback to the MUSIC pool is allowed only for general MUSIC/ALL slots.
                            for(int attempt=0;attempt<candidates.Count;attempt++){
                                int index=(cursor+attempt)%candidates.Count;
                                var c=candidates[index];
                                if(!matches(c))continue;
                                if(lastTitle.TryGetValue(c.path,out var lastT)&&(planned-lastT).TotalMinutes<titleGap)continue;
                                if(lastArtist.TryGetValue(c.artist,out var lastA)&&(planned-lastA).TotalMinutes<artistGap)continue;
                                selected=index;break;
                            }
                            if(selected<0 && (desired=="MUSIC"||desired=="ALL") && candidates.Count>0){
                                // Emergency selection: relax artist/title separation to keep the plan populated.
                                for(int offset=0;offset<candidates.Count;offset++){
                                    int idx=(cursor+offset)%candidates.Count;
                                    if(matches(candidates[idx])){selected=idx;fallbackCount++;break;}
                                }
                            }
                            if(selected<0){violations++;sb.AppendLine(planned.ToString("HH:mm")+"  UNFILLED "+desired+" (rotation/category)");continue;}
                            var chosen=candidates[selected];cursor=(selected+1)%candidates.Count;
                            if(!durations.ContainsKey(chosen.path))estimatedCount++;
                            lastTitle[chosen.path]=planned;lastArtist[chosen.artist]=planned;
                            entries.Add((planned,chosen.path,desired,chosen.duration));
                            sb.AppendLine(planned.ToString("HH:mm")+"  "+desired.PadRight(12)+
                                " "+System.IO.Path.GetFileNameWithoutExtension(chosen.path)+
                                " ["+TimeSpan.FromSeconds(chosen.duration).ToString(@"mm\:ss")+"]");
                        }
                    }
                    sb.AppendLine("--------------------------------------------------------------");
                    sb.AppendLine($"Scheduled={entries.Count} · Unfilled={violations} · Missing files={missing} · Library={snapshot.Length}");
                    sb.AppendLine($"Rotation fallbacks={fallbackCount} · Estimated durations={estimatedCount} · Invalid clock lines={invalidClockLines.Count} · Duplicate clock slots={duplicateClockSlots}");
                    sb.AppendLine($"Unmapped library tracks={unmapped} · Mapped categories={mappings.Count}");
                    if(mappings.Count==0)sb.AppendLine("WARNING: CATEGORY_MAP is empty. JINGLE/STATION ID slots cannot resolve automatically.");
                    foreach(var invalid in invalidClockLines.Take(20))sb.AppendLine("INVALID CLOCK: "+invalid);
                    if(categoryLines.Length==0)sb.AppendLine("WARNING: No configured categories.");
                    sb.AppendLine("WARNING: slot-based plan; not a gapless broadcast-ready schedule.");
                    var csvBuilder=new StringBuilder("PlannedTime,Category,DurationSeconds,Path\r\n");
                    string Quote(string x)=>"\""+x.Replace("\"","\"\"")+"\"";
                    foreach(var e in entries)csvBuilder.AppendLine(
                        Quote(e.time.ToString("yyyy-MM-dd HH:mm:ss"))+","+
                        Quote(e.category)+","+e.duration.ToString(System.Globalization.CultureInfo.InvariantCulture)+","+
                        Quote(e.path));
                    return (entries,sb.ToString(),csvBuilder.ToString());
                });
                generated=result.entries;output.Text=result.Item2;logText=result.Item2;csv=result.Item3;
                apply.IsEnabled=generated.Count>0;
                LogDiagnostic("SCHEDULER",$"Generated {generated.Count} slots; {number} hours; no on-air changes");
            }catch(Exception ex){output.Text="Scheduler error: "+ex.Message;LogDiagnostic("ERROR","PRO scheduler: "+ex);}
            finally{generate.IsEnabled=true;}
        };
        saveLog.Click+=(_,_)=>{
            if(logText.Length==0)return;
            try{
                Directory.CreateDirectory(proDataDirectory);
                string file=System.IO.Path.Combine(proDataDirectory,"FINAL_LOG_LAST.txt");
                File.WriteAllText(file,logText,Encoding.UTF8);
                LogDiagnostic("SCHEDULER","Final log saved: "+file);
                MessageBox.Show("Το Final Log αποθηκεύτηκε στον φάκελο PRO.","KYMA PRO");
            }catch(Exception ex){LogDiagnostic("ERROR","Final log save: "+ex);MessageBox.Show(ex.Message,"KYMA PRO");}
        };
        export.Click+=(_,_)=>{
            if(csv.Length==0)return;
            var dialog=new SaveFileDialog { Filter="CSV files|*.csv",FileName="KYMA_FINAL_LOG_"+DateTime.Now.ToString("yyyyMMdd_HHmm")+".csv" };
            if(dialog.ShowDialog()==true)File.WriteAllText(dialog.FileName,csv,new UTF8Encoding(true));
        };
        apply.IsEnabled=false;
        apply.Click+=(_,_)=>{
            if(generated.Count==0)return;
            if(MessageBox.Show("Προσθήκη "+generated.Count+" εγγραφών στο ΤΕΛΟΣ της playlist; Δεν προγραμματίζει αυτόματη εκτέλεση.",
                "KYMA PRO",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            int added=0;
            var existing=new HashSet<string>(queue.Select(x=>x.Path),StringComparer.OrdinalIgnoreCase);
            foreach(var entry in generated){
                if(preventQueueDuplicates&&!existing.Add(entry.path))continue;
                queue.Add(new Track { Path=entry.path });added++;
            }
            UpdateNext();SaveState();
            LogDiagnostic("SCHEDULER",$"Appended {added} tracks manually to playlist");
            MessageBox.Show("Προστέθηκαν "+added+" κομμάτια. Οι προγραμματισμένες ώρες δεν εφαρμόζονται στην αναπαραγωγή.","KYMA PRO");
        };
        w.Show();
    }
    private void OpenProOperations(){
        Directory.CreateDirectory(proDataDirectory);
        var w=new Window { Title="KYMA RADIO AUTOMATION PRO | OPERATIONS",
            Width=1060,Height=720,MinWidth=740,MinHeight=490,
            Background=new SolidColorBrush(Color.FromRgb(17,25,36)),
            Foreground=Brushes.White,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var root=new DockPanel { Margin=new Thickness(14) };
        var title=new TextBlock { Text="KYMA PRO  ·  OPERATIONS CENTER",FontSize=21,
            FontWeight=FontWeights.Bold,Foreground=Brushes.White,Margin=new Thickness(0,0,0,10) };
        DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);
        var note=new TextBlock { Text="Εργαλεία διαχείρισης · Δεν επηρεάζουν την ενεργή αναπαραγωγή. Τα clocks και οι καμπάνιες δεν εκτελούνται αυτόματα ακόμη.",
            TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(170,197,218)),Margin=new Thickness(0,0,0,12) };
        DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
        var panel=new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height=new GridLength(1,GridUnitType.Star) });
        var buttons=new WrapPanel { Margin=new Thickness(0,0,0,12) };
        Grid.SetRow(buttons,0);panel.Children.Add(buttons);
        var editor=new TextBox { AcceptsReturn=true,AcceptsTab=true,TextWrapping=TextWrapping.NoWrap,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            FontFamily=new FontFamily("Consolas"),FontSize=13 };
        Grid.SetRow(editor,1);panel.Children.Add(editor);
        root.Children.Add(panel);w.Content=root;
        string currentDocument="";
        string SafeName(string name)=>System.IO.Path.Combine(proDataDirectory,name+".txt");
        void OpenDocument(string name,string template){
            currentDocument=name;
            string path=SafeName(name);
            editor.Text=File.Exists(path)?File.ReadAllText(path):template;
            w.Title="KYMA PRO | "+name;
        }
        void Add(string label,Action action){
            var button=new Button { Content=label,Padding=new Thickness(12,8,12,8),
                Margin=new Thickness(0,0,7,7),Background=new SolidColorBrush(Color.FromRgb(39,75,106)),
                Foreground=Brushes.White };
            button.Click+=(_,_)=>{try{action();}catch(Exception ex){
                MessageBox.Show(ex.Message,"KYMA PRO",MessageBoxButton.OK,MessageBoxImage.Warning);
                LogDiagnostic("ERROR","PRO operations: "+ex);
            }};
            buttons.Children.Add(button);
        }
        // 1: persisted hourly clock template editor
        Add("1 · CLOCKS",()=>OpenDocument("CLOCKS",
            "# Ωριαίο Clock (πρότυπο, όχι ενεργό scheduler)\\n00:00 MUSIC\n00:15 JINGLE\n00:30 MUSIC\n00:45 STATION ID\\n"));
        // 2: persisted rotation and separation rules
        Add("2 · ROTATION",()=>OpenDocument("ROTATION",
            "# Μουσικοί κανόνες (πρότυπο)\\nARTIST_SEPARATION_MIN=90\nTITLE_SEPARATION_MIN=240\\n"));
        // 3: category definitions
        Add("3 · CATEGORIES",()=>OpenDocument("CATEGORIES",
            "# Κατηγορίες\\nGOLD\nCURRENT\nGREEK\nINTERNATIONAL\nJINGLES\\n"));
        Add("CATEGORY MAP",()=>OpenDocument("CATEGORY_MAP",
            "# CATEGORY|filename substring (π.χ. JINGLES|Station ID)\\n"));
        // 4: commercial clients
        Add("4 · CLIENTS",()=>OpenDocument("CLIENTS",
            "# Πελάτες · ένα όνομα ανά γραμμή\\n"));
        // 5: spot campaign manifest
        Add("5 · CAMPAIGNS",()=>OpenDocument("CAMPAIGNS",
            "# Καμπάνιες · CSV: client,spot,start_date,end_date,plays_per_day\\n"));
        // 6: break templates
        Add("6 · AD BREAKS",()=>OpenDocument("AD_BREAKS",
            "# Πρότυπα διαφημιστικών διαλειμμάτων (όχι ενεργά)\\n08:00\\n12:00\\n18:00\\n"));
        // 7: jingle and voice tracking rundown notes
        Add("7 · VOICE / JINGLES",()=>OpenDocument("VOICE_TRACKING",
            "# Σημειώσεις voice tracking / jingle placements\\n"));
        // 8: real offline schedule generation and CSV export
        Add("8 · FINAL LOG",OpenProScheduler);
        // 9: read-only real playback history
        Add("9 · AS-RUN",()=>{
            currentDocument="";
            editor.Text="KYMA PRO · PLAYBACK HISTORY (session/state)"+Environment.NewLine+
                string.Join(Environment.NewLine,history);
        });
        // 10: generate exportable diagnostics
        Add("10 · HEALTH",()=>{
            currentDocument="";
            RecordMemorySample();
            editor.Text=$"KYMA PRO · BROADCAST HEALTH · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"+
                Environment.NewLine+$"Crossfades: {completedCrossfades}"+
                Environment.NewLine+$"Preload ready: {preloadReadyCount}"+
                Environment.NewLine+$"Preload average ms: {(preloadReadyCount>0?preloadTotalMs/preloadReadyCount:0):F0}"+
                Environment.NewLine+$"Slow opens: {slowOpenTotal}"+
                Environment.NewLine+$"UI lag events: {uiLagEvents}"+
                Environment.NewLine+$"Peak RAM MB: {peakMemoryMb}"+
                Environment.NewLine+$"Main recoveries: {mainRecoveryAttempts}"+
                Environment.NewLine+$"Standby retries: {standbyRecoveryAttempts}";
        });
        Add("SAVE",()=>{
            if(string.IsNullOrEmpty(currentDocument)){
                MessageBox.Show("Επέλεξε πρώτα ένα επεξεργάσιμο αρχείο.","KYMA PRO");return;
            }
            string path=SafeName(currentDocument);
            string temporary=path+".tmp";
            File.WriteAllText(temporary,editor.Text,Encoding.UTF8);
            File.Move(temporary,path,true);
            LogDiagnostic("PRO","Saved "+currentDocument);
        });
        Add("EXPORT TXT",()=>{
            var dialog=new SaveFileDialog { Filter="Text file|*.txt",FileName=
                (string.IsNullOrEmpty(currentDocument)?"KYMA_PRO_REPORT":currentDocument)+".txt" };
            if(dialog.ShowDialog()==true)File.WriteAllText(dialog.FileName,editor.Text,Encoding.UTF8);
        });
        Add("LAST FINAL LOG",()=>OpenDocument("FINAL_LOG_LAST",
            "# Δεν έχει αποθηκευτεί Final Log ακόμη.\\n"));
        Add("BACKUP PRO",()=>{
            var dialog=new SaveFileDialog { Filter="ZIP archive|*.zip",FileName="KYMA_PRO_BACKUP_"+DateTime.Now.ToString("yyyyMMdd_HHmm")+".zip" };
            if(dialog.ShowDialog()!=true)return;
            if(File.Exists(dialog.FileName))File.Delete(dialog.FileName);
            System.IO.Compression.ZipFile.CreateFromDirectory(proDataDirectory,dialog.FileName);
            LogDiagnostic("PRO","PRO documents backup created");
        });
        Add("OPEN PRO FOLDER",()=>{
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName=proDataDirectory,UseShellExecute=true });
        });
        Add("RESTORE PRO",()=>{
            var dialog=new OpenFileDialog { Filter="ZIP archive|*.zip" };
            if(dialog.ShowDialog()!=true)return;
            if(MessageBox.Show("Επαναφορά PRO εγγράφων από backup; Τα υπάρχοντα αρχεία μπορεί να αντικατασταθούν.",
                "KYMA PRO",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            // Only plain top-level TXT documents are restored, preventing ZIP path traversal.
            using var archive=System.IO.Compression.ZipFile.OpenRead(dialog.FileName);
            foreach(var entry in archive.Entries){
                if(entry.FullName.Contains('/')||entry.FullName.Contains('\\')||
                   !entry.Name.EndsWith(".txt",StringComparison.OrdinalIgnoreCase))continue;
                string destination=System.IO.Path.Combine(proDataDirectory,entry.Name);
                entry.ExtractToFile(destination,true);
            }
            currentDocument="";editor.Text="Η επαναφορά ολοκληρώθηκε. Άνοιξε ξανά το έγγραφο.";
            LogDiagnostic("PRO","PRO documents restored from backup");
        });
        w.Show();
    }
    private void OpenDiagnostics() {
        if(diagnosticsWindow!=null){diagnosticsWindow.Activate();return;}
        var w=new Window { Title="KYMA | DEBUG / DIAGNOSTICS",Width=900,Height=580,MinWidth=580,MinHeight=350,
            Background=new SolidColorBrush(Color.FromRgb(17,25,36)),Foreground=Brushes.White,Owner=this,
            WindowStartupLocation=WindowStartupLocation.CenterOwner };
        var root=new DockPanel { Margin=new Thickness(12) };
        var bar=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,8) };
        Button Action(string title,Action action) {
            var button=new Button { Content=title,Padding=new Thickness(14,7,14,7),Margin=new Thickness(0,0,8,0),
                Background=new SolidColorBrush(Color.FromRgb(40,69,94)),Foreground=Brushes.White };
            button.Click+=(_,_)=>action();bar.Children.Add(button);return button;
        }
        Action("KYMA PRO",OpenProOperations);
        Action("CLOCK PREVIEW",OpenClockPreview);
        Action("TEST NAS NEXT",()=>{
            int next=FindUpcomingIndex();
            if(next>=0)DiagnoseSlowNasFile(queue[next].Path,0);
            else LogDiagnostic("NAS","No upcoming track to test");
        });
        Action("BROADCAST REPORT",()=>{
            RecordMemorySample();
            LogDiagnostic("BROADCAST REPORT",$"Crossfades={completedCrossfades} · short jingles={shortJingleTransitions} · preload ready={preloadReadyCount} · avg preload={(preloadReadyCount>0?preloadTotalMs/preloadReadyCount:0):F0}ms · slow opens={slowOpenTotal}");
            LogDiagnostic("BROADCAST REPORT",$"UI lag events >=250ms={uiLagEvents} · worst={worstUiLagMs:F0}ms · main recoveries={mainRecoveryAttempts} · standby retries={standbyRecoveryAttempts} · preload warnings={preloadWaitWarnings}");
            LogDiagnostic("BROADCAST REPORT",$"RAM={lastMemoryMb}MB · peak={peakMemoryMb}MB · skipped missing checks={skippedMissingFiles} · playback={(stopped?"STOPPED":paused?"PAUSED":"ACTIVE")}");
        });
        Action("PRELOAD STATS",()=>{
            LogDiagnostic("PRELOAD STATS",$"Ready={preloadReadyCount} · average={(preloadReadyCount>0?preloadTotalMs/preloadReadyCount:0):F0}ms · max={preloadMaxMs:F0}ms · warnings={preloadWaitWarnings} · missing checks={skippedMissingFiles}");
        });
        Action("AUDIO HEALTH",()=>{
            LogDiagnostic("AUDIO HEALTH",$"Crossfades={completedCrossfades} · slow opens >3s={slowOpenTotal} · preload warnings={preloadWaitWarnings} · ready={preloadReady} · fading={fading} · RAM={MemoryMb()} MB");
            foreach(var item in slowOpenCounts.OrderByDescending(x=>x.Value).Take(5))
                LogDiagnostic("AUDIO HEALTH",$"Slow {item.Value}x: {System.IO.Path.GetFileName(item.Key)}");
        });
        Action("MEMORY SUMMARY",()=>{
            RecordMemorySample();
            LogDiagnostic("MEMORY",$"Current={lastMemoryMb} MB · baseline={baselineMemoryMb} MB · peak={peakMemoryMb} MB · delta={lastMemoryMb-baselineMemoryMb:+#;-#;0} MB · completed crossfades={completedCrossfades} · stable samples={stableMemorySamples}");
        });
        Action("CANCEL CLEAN / IMPORT",CancelBackgroundJobs);
        Action("OPEN ERROR LOG",()=>{
            if(File.Exists(errorLogPath))Process.Start(new ProcessStartInfo(errorLogPath){UseShellExecute=true});
            else LogDiagnostic("LOG","No persisted errors yet");
        });
        Action("COPY LOG",()=>Clipboard.SetText(string.Join(Environment.NewLine,diagnosticLines)));
        Action("SAVE LOG",()=>{
            var dialog=new SaveFileDialog { Filter="Text log|*.log|Text file|*.txt",FileName="KYMA_Diagnostics_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".log" };
            if(dialog.ShowDialog(w)==true)try{File.WriteAllLines(dialog.FileName,diagnosticLines);LogDiagnostic("LOG","Saved to "+dialog.FileName);}
            catch(Exception ex){LogDiagnostic("ERROR","Log save failed: "+ex.Message);}
        });
        Action("CLEAR VIEW",()=>{diagnosticLines.Clear();diagnosticsText?.Clear();LogDiagnostic("LOG","View cleared");});
        DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
        diagnosticsText=new TextBox { IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
            FontFamily=new FontFamily("Consolas"),FontSize=12,Background=new SolidColorBrush(Color.FromRgb(9,17,27)),
            Foreground=new SolidColorBrush(Color.FromRgb(177,228,211)),BorderThickness=new Thickness(1),
            Text=string.Join(Environment.NewLine,diagnosticLines) };
        root.Children.Add(diagnosticsText);w.Content=root;diagnosticsWindow=w;
        w.Closed+=(_,_)=>{diagnosticsWindow=null;diagnosticsText=null;};
        w.Show();diagnosticsText.ScrollToEnd();
    }
    private readonly ObservableCollection<Track> library = new();
    private readonly ObservableCollection<Track> queue = new();
    private readonly FastTrackCollection filtered = new();
    private readonly FastTrackCollection filteredMusic = new();
    private readonly FastTrackCollection filteredJingles = new();
    private readonly Dictionary<string,string> mediaCategoryOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string CategoryOverridesFile => System.IO.Path.Combine(proDataDirectory,"MEDIA_CATEGORIES.tsv");
    private string GetMediaCategory(string path){
        if(mediaCategoryOverrides.TryGetValue(path,out var category))return category;
        var segments=path.Replace('/','\\').Split('\\');
        foreach(var segment in segments){
            if(segment.Equals("JINGLES",StringComparison.OrdinalIgnoreCase)||segment.Equals("JINGLE",StringComparison.OrdinalIgnoreCase)||segment.Equals("IDS",StringComparison.OrdinalIgnoreCase))return "JINGLES";
            if(segment.Equals("SPOTS",StringComparison.OrdinalIgnoreCase)||segment.Equals("COMMERCIALS",StringComparison.OrdinalIgnoreCase)||segment.Equals("ADS",StringComparison.OrdinalIgnoreCase))return "SPOTS";
        }
        return "MUSIC";
    }
    private void LoadMediaCategories(){
        try{
            if(!File.Exists(CategoryOverridesFile))return;
            foreach(var line in File.ReadLines(CategoryOverridesFile)){
                int split=line.IndexOf('\t');if(split<=0)continue;
                string cat=line.Substring(0,split);
                if(cat=="MUSIC"||cat=="JINGLES"||cat=="SPOTS")mediaCategoryOverrides[line.Substring(split+1)]=cat;
            }
        }catch(Exception ex){LogDiagnostic("ERROR","Media categories load: "+ex.Message);}
    }
    private void SetMediaCategory(Track track,string category){
        mediaCategoryOverrides[track.Path]=category;
        try{
            Directory.CreateDirectory(proDataDirectory);
            var temp=CategoryOverridesFile+".tmp";
            File.WriteAllLines(temp,mediaCategoryOverrides.Select(kv=>kv.Value+"\t"+kv.Key),Encoding.UTF8);
            File.Move(temp,CategoryOverridesFile,true);
            RefreshLibrary();StatusText.Text="CATEGORY: "+category+" · "+track.Display;
        }catch(Exception ex){LogDiagnostic("ERROR","Media category save: "+ex.Message);MessageBox.Show(ex.Message,"KYMA");}
    }
    private void ConfigureLibraryCategories(){
        LoadMediaCategories();
        foreach(var list in new[]{LibraryList,LibraryListA,LibraryListB}){
            var menu=new ContextMenu();
            foreach(var cat in new[]{"MUSIC","JINGLES","SPOTS"}){
                string chosen=cat;
                var item=new MenuItem{Header="Κατηγορία → "+cat};
                item.Click+=(_,_)=>{if(list.SelectedItem is Track t)SetMediaCategory(t,chosen);};
                menu.Items.Add(item);
            }
            list.ContextMenu=menu;
        }
    }
    private readonly ObservableCollection<string> history = new();
    private MediaPlayer player = new();
    private MediaPlayer incomingPlayer = new();
    private bool fading = false;
    private double crossfadeSeconds = 5;
    private double outgoingFadeEnd = 0;
    private double fadeStartPosition = 0;
    private double outgoingFadeDuration = 5;
    private DateTime lastFadeClock = DateTime.UtcNow;
    private double fadeElapsedSeconds = 0;
    private bool pendingStateSave = false;
    private readonly DispatcherTimer saveDebounce = new() { Interval=TimeSpan.FromSeconds(2) };
    private bool loadingState = false;
    private bool stateDirty = false;
    private string? preloadedPath;
    private bool preloadReady;
    private DateTime preloadStartedAt = DateTime.MinValue;
    private string? preloadWarningPath;
    private DateTime lastPreloadWarningUtc=DateTime.MinValue;
    private readonly Dictionary<string,int> slowOpenCounts=new(StringComparer.OrdinalIgnoreCase);
    private int slowOpenTotal=0;
    private int nasProbeCount=0;
    private async void DiagnoseSlowNasFile(string path,double mediaOpenMs){
        nasProbeCount++;
        int probeId=nasProbeCount;
        LogDiagnostic("NAS",$"PROBE #{probeId} started for {System.IO.Path.GetFileName(path)} · MediaOpened={mediaOpenMs:F0}ms");
        try {
            var result=await Task.Run(()=>{
                var sw=Stopwatch.StartNew();
                var info=new FileInfo(path);
                long length=info.Length;
                double metadataMs=sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                byte[] buffer=new byte[65536];
                int bytes;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite,
                    65536,FileOptions.SequentialScan))
                    bytes=stream.Read(buffer,0,buffer.Length);
                double firstReadMs=sw.Elapsed.TotalMilliseconds;
                string extension=System.IO.Path.GetExtension(path).ToLowerInvariant();
                string signature=bytes>=4?Convert.ToHexString(buffer,0,Math.Min(bytes,12)):"empty";
                bool id3=bytes>=3&&buffer[0]==73&&buffer[1]==68&&buffer[2]==51;
                long id3Size=0;
                if(id3&&bytes>=10)
                    id3Size=10L+((long)(buffer[6]&127)<<21)+((long)(buffer[7]&127)<<14)+((long)(buffer[8]&127)<<7)+(buffer[9]&127);
                return (length,metadataMs,firstReadMs,bytes,extension,signature,id3Size);
            });
            LogDiagnostic("NAS",$"PROBE #{probeId} {System.IO.Path.GetFileName(path)} · size={result.length/1048576.0:F2} MiB · metadata={result.metadataMs:F0}ms · read64K={result.firstReadMs:F0}ms · bytes={result.bytes} · type={result.extension} · ID3v2={result.id3Size}B · header={result.signature} · MediaOpened={mediaOpenMs:F0}ms");
            if(result.metadataMs>500||result.firstReadMs>500)
                LogDiagnostic("NAS",$"PROBE #{probeId}: slow file access detected; investigate SMB/NAS");
            else
                LogDiagnostic("NAS",$"PROBE #{probeId}: file access fast at probe time; media decoder or earlier transient SMB delay remains possible");
        }catch(Exception ex){LogDiagnostic("NAS",$"PROBE #{probeId} failed: {ex.GetType().Name}: {ex.Message}");}
    }
    private int preloadWaitWarnings=0;
    private int preloadReadyCount=0;
    private double preloadTotalMs=0;
    private double preloadMaxMs=0;
    private int skippedMissingFiles=0;
    private int uiLagEvents=0;
    private double worstUiLagMs=0;
    private int shortJingleTransitions=0;
    private int mainRecoveryAttempts=0;
    private int standbyRecoveryAttempts=0;
    private DateTime lastMissingLogUtc=DateTime.MinValue;
    private void CheckPreloadHealth(){
        if(stopped||paused||!auto||fading)return;
        int upcoming=FindUpcomingIndex();
        if(upcoming<0)return;
        string path=queue[upcoming].Path;
        double remaining=EffectiveEndSeconds()-player.Position.TotalSeconds;
        if(remaining<=0)return;
        bool pending=string.Equals(preloadedPath,path,StringComparison.OrdinalIgnoreCase);
        if(pending&&preloadReady){preloadWarningPath=null;return;}
        // Very short jingles cannot preload until the previous fade completes.
        double typicalOpenSeconds=preloadReadyCount>0?preloadTotalMs/preloadReadyCount/1000.0:1.5;
        double warningWindow=Math.Max(12,Math.Max(crossfadeSeconds+8,Math.Min(20,typicalOpenSeconds*3+crossfadeSeconds)));
        if(remaining<=warningWindow &&
           !(CurrentDurationSeconds()<5 && pending && (DateTime.UtcNow-preloadStartedAt).TotalSeconds<2) &&
           (preloadWarningPath!=path||(DateTime.UtcNow-lastPreloadWarningUtc).TotalSeconds>=15)){
            preloadWarningPath=path;lastPreloadWarningUtc=DateTime.UtcNow;preloadWaitWarnings++;
            LogDiagnostic("PRELOAD",$"WARNING #{preloadWaitWarnings}: NEXT not ready · {System.IO.Path.GetFileName(path)} · remaining={remaining:F1}s · pending={pending}");
            StatusText.Text="NEXT AUDIO NOT READY";
        }
    }
    private readonly System.Threading.SemaphoreSlim waveformWorker = new(1,1);
    private int analysisGeneration = 0;
    private double silenceThresholdDb = -42;
    private readonly Dictionary<string,double> manualMixOut = new(StringComparer.OrdinalIgnoreCase);
    private double AudioEnd(string path,double full) {
        if(manualMixOut.TryGetValue(path,out var manual))return Math.Clamp(manual,1,full);
        return audibleEnds.TryGetValue(path,out var end)?Math.Clamp(end,1,full):full;
    }
    private readonly Dictionary<string,double> audibleEnds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,double> decodedDurations = new(StringComparer.OrdinalIgnoreCase);
    private double CurrentDurationSeconds() {
        if(player.NaturalDuration.HasTimeSpan)return player.NaturalDuration.TimeSpan.TotalSeconds;
        if(activeIndex>=0&&activeIndex<queue.Count&&decodedDurations.TryGetValue(queue[activeIndex].Path,out var duration))return duration;
        return 0;
    }
    private double EffectiveEndSeconds() {
        if(activeIndex<0||activeIndex>=queue.Count)return 0;
        var full=CurrentDurationSeconds();
        if(full<=0)return 0;
        return AudioEnd(queue[activeIndex].Path,full);
    }
    private bool preventQueueDuplicates = true;
    private readonly MediaPlayer[] cartPlayers = Enumerable.Range(0,9).Select(_=>new MediaPlayer()).ToArray();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly string?[] carts = new string?[9];
    private int activeIndex = -1;
    private bool auto = false;
    private bool stopped = true;
    private bool movingNext = false;
    private bool paused = false;
    private bool repeatPlaylist = false;
    private readonly DispatcherTimer searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool importing = false;
    private bool refreshingLibrary = false;
    private readonly HashSet<string> libraryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random random = new();
    private double playbackVolume = 0.85;
    private readonly string[] audioExtensions = { ".mp3", ".wav", ".wma", ".aac", ".m4a", ".flac" };
    private readonly string stateFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KYMA_SOHO", "library.json");
    public MainWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = queue; LibraryList.ItemsSource = filtered; LibraryListA.ItemsSource = filteredMusic; LibraryListB.ItemsSource = filteredJingles; ConfigureLibraryCategories(); HistoryList.ItemsSource = history;
        for (int i=0;i<9;i++) { var n=i; var b=new Button { Content=$"CART {i+1}", Background=new SolidColorBrush(Color.FromRgb((byte)(65+i*10),(byte)(37+i*6),(byte)(61+i*4))), Tag=i, FontSize=14, FontWeight=FontWeights.Bold, Foreground=Brushes.White, BorderBrush=new SolidColorBrush(Color.FromRgb(85,112,143)), BorderThickness=new Thickness(1), Margin=new Thickness(3), ToolTip="Αριστερό κλικ: PLAY · Δεξί κλικ: φόρτωση Cart" }; b.Click += (_,_)=> { if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))StopSingleCart(n);else PlayCart(n); }; b.MouseRightButtonUp += (_,_)=>AssignCart(n); CartGrid.Children.Add(b); cartPlayers[n].MediaEnded += (_,_)=>Dispatcher.BeginInvoke(()=>UpdateCartPad(n,false)); }
        searchDebounce.Tick += (_,_) => { searchDebounce.Stop(); RefreshLibrary(); };
        saveDebounce.Tick += (_,_)=>{saveDebounce.Stop();if(fading){pendingStateSave=true;return;}FlushState();};
        ConfigureAudioMenus();
        var debugButton=new Button { Content="DEBUG",Padding=new Thickness(12,5,12,5),
            Background=new SolidColorBrush(Color.FromRgb(45,83,107)),Foreground=Brushes.White,
            ToolTip="Diagnostics: χρόνοι, σφάλματα, καθυστερήσεις και καταγραφή συμβάντων" };
        debugButton.Click+=(_,_)=>OpenDiagnostics();
        var cancelButton=new Button {Content="CANCEL JOB",Padding=new Thickness(10,5,10,5),
            Background=new SolidColorBrush(Color.FromRgb(110,54,53)),Foreground=Brushes.White,
            ToolTip="Ακύρωση CLEAN ή IMPORT χωρίς κλείσιμο του KYMA"};
        cancelButton.Click+=(_,_)=>CancelBackgroundJobs();
        if(StatusText.Parent is Panel statusPanel){statusPanel.Children.Add(debugButton);statusPanel.Children.Add(cancelButton);}
        else {
            var debugMenu=new MenuItem { Header="DEBUG / DIAGNOSTICS" };
            debugMenu.Click+=(_,_)=>OpenDiagnostics();
            ContextMenu ??= new ContextMenu();
            ContextMenu.Items.Add(debugMenu);
        }
        LogDiagnostic("STARTUP","UI initialized");
        Title="KYMA RADIO AUTOMATION | SOHO Professional 3.5 BETA";
        EnableVirtualizedLists();
        Loaded += (_,_)=>{UpdateVersionLabels(this);LogDiagnostic("STARTUP",$"Window loaded in {startupClock.ElapsedMilliseconds} ms; library={library.Count:N0}; queue={queue.Count:N0}");};
        Dispatcher.UnhandledException+=(_,e)=>{LogDiagnostic("ERROR","Unhandled UI exception: "+e.Exception);};
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>PersistDiagnosticError("FATAL: "+e.ExceptionObject);
        TaskScheduler.UnobservedTaskException+=(_,e)=>PersistDiagnosticError("TASK: "+e.Exception);
        player.MediaEnded += Player_MediaEnded;
        incomingPlayer.MediaEnded += Player_MediaEnded;
        player.MediaFailed += (_,e)=>Dispatcher.BeginInvoke(() => {
            StatusText.Text="PLAYBACK ERROR: "+e.ErrorException.Message;
            if(auto && !stopped && consecutiveAudioFailures<3){
                consecutiveAudioFailures++;
                mainRecoveryAttempts++;
                LogDiagnostic("RECOVERY",$"MAIN failed; skip to next (attempt {consecutiveAudioFailures}/3, total={mainRecoveryAttempts})");
                NextTrack();
            }else {stopped=true;LogDiagnostic("ERROR","MAIN stopped after playback failure");}
        });
        player.MediaOpened += Player_MediaOpened; incomingPlayer.MediaOpened += Player_MediaOpened;
        player.MediaOpened+=MediaOpenedDiagnostic;
        incomingPlayer.MediaOpened+=MediaOpenedDiagnostic;
        player.MediaFailed+=(_,e)=>LogDiagnostic("ERROR","Playback: "+e.ErrorException);
        incomingPlayer.MediaFailed+=(_,e)=>LogDiagnostic("ERROR","Preload: "+e.ErrorException);
        incomingPlayer.MediaFailed += (_,e)=>Dispatcher.BeginInvoke(()=>{
            preloadReady=false;preloadedPath=null;
            if(fading){FinishCrossfade();StatusText.Text="NEXT AUDIO ERROR";}
            else {
                StatusText.Text="PRELOAD ERROR: "+e.ErrorException.Message;
                var failedPath=openRequests.TryGetValue(incomingPlayer,out var pending)?pending.path:lastStandbyFailedPath;
                if(failedPath!=lastStandbyFailedPath){lastStandbyFailedPath=failedPath;standbyRetryCount=0;}
                if(auto&&!stopped&&standbyRetryCount<1){
                    standbyRetryCount++;
                    standbyRecoveryAttempts++;
                    LogDiagnostic("RECOVERY",$"Standby retry scheduled once · total={standbyRecoveryAttempts} · file={System.IO.Path.GetFileName(failedPath)}");
                    var retryTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};
                    retryTimer.Tick+=(_,_)=>{
                        retryTimer.Stop();
                        if(!stopped&&!fading&&preloadedPath==null)PreloadUpcoming();
                    };
                    retryTimer.Start();
                }
            }
        });
        timer.Tick += (_,_)=> {
            if(fading) {
                var clockNow=DateTime.UtcNow;
                if(!paused)fadeElapsedSeconds+=Math.Clamp((clockNow-lastFadeClock).TotalSeconds,0,0.5);
                lastFadeClock=clockNow;
                ApplyFadeVolumes();
                if(fadeElapsedSeconds>=outgoingFadeDuration)FinishCrossfade();
            } else if(auto&&!stopped&&!paused&&crossfadeSeconds>0&&CurrentDurationSeconds()>0) {
                double remaining=EffectiveEndSeconds()-player.Position.TotalSeconds;
                if(remaining<=crossfadeSeconds)BeginCrossfade();
            }
            CheckPreloadHealth();
            if(pendingStateSave&&!fading){pendingStateSave=false;SaveState();}
            UpdateFadeCountdown();UpdateWavePlayhead();Clock.Text=DateTime.Now.ToString("HH:mm:ss"); if(CurrentDurationSeconds()>0 && !stopped) NowTime.Text=$"{player.Position.ToString(@"mm\:ss")} / {TimeSpan.FromSeconds(CurrentDurationSeconds()).ToString(@"mm\:ss")}"; else NowTime.Text="00:00"; if(!stopped && player.NaturalDuration.HasTimeSpan) PlaybackProgress.Value=Math.Min(PlaybackProgress.Maximum,player.Position.TotalSeconds); };
        responsivenessTimer.Tick+=(_,_)=>{
            var now=DateTime.UtcNow;var gap=(now-lastHeartbeat).TotalMilliseconds;
            double lagMs=Math.Max(0,gap-1000);
            if(lagMs>=250){
                uiLagEvents++;
                worstUiLagMs=Math.Max(worstUiLagMs,lagMs);
                LogDiagnostic("UI",$"Lag event #{uiLagEvents}: {lagMs:F0}ms · audio={(stopped?"STOPPED":"ACTIVE")}");
            }
            if(gap>2300)LogDiagnostic("UI",$"Dispatcher delayed {gap:F0} ms");
            if((now-lastPerformanceLog).TotalSeconds>=10){
                diagnosticsProcess.Refresh();
                var cpu=diagnosticsProcess.TotalProcessorTime;
                var elapsed=(now-lastCpuSample).TotalSeconds;
                var cpuPct=elapsed>0?100*(cpu-lastCpuTime).TotalSeconds/(elapsed*Environment.ProcessorCount):0;
                RecordMemorySample();
                LogDiagnostic("PERF",$"CPU {cpuPct:F1}% · RAM {lastMemoryMb:N0} MB · peak {peakMemoryMb:N0} MB · UI lag {Math.Max(0,gap-1000):F0} ms · audio={(stopped?"STOPPED":"ACTIVE")} · fades={completedCrossfades}");
                if(completedCrossfades>0 && completedCrossfades%5==0 && stableMemorySamples==1)
                    LogDiagnostic("MEMORY",$"After {completedCrossfades} fades: RAM={lastMemoryMb} MB, baseline={baselineMemoryMb} MB, peak={peakMemoryMb} MB");
                lastCpuTime=cpu;lastCpuSample=now;lastPerformanceLog=now;
            }
            lastHeartbeat=now;
        };
        responsivenessTimer.Start();
        player.Volume=playbackVolume; incomingPlayer.Volume=playbackVolume; foreach(var cp in cartPlayers)cp.Volume=playbackVolume; timer.Start(); var loadWatch=Stopwatch.StartNew(); LoadState(); LogDiagnostic("LIBRARY",$"Restored {library.Count:N0} tracks in {loadWatch.ElapsedMilliseconds} ms"); UpdateNext(); DrawWave(NowWaveform, null); DrawWave(NextWaveform, null);
        Closing += (_,_)=> {var closeWatch=Stopwatch.StartNew();responsivenessTimer.Stop();saveDebounce.Stop();FlushState();player.Close();incomingPlayer.Close();foreach(var cp in cartPlayers)cp.Close();LogDiagnostic("SHUTDOWN",$"Cleanup completed in {closeWatch.ElapsedMilliseconds} ms");};
    }
    private static void UpdateVersionLabels(DependencyObject root) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is TextBlock label && label.Text.Contains("BROADCAST CONSOLE v2.2.1 BETA"))
                label.Text=label.Text.Replace("v2.2.1 BETA","v3.5 BETA");
            UpdateVersionLabels(child);
        }
    }
    private void EnableVirtualizedLists() {
        foreach(var list in new[]{LibraryList,LibraryListA,LibraryListB,QueueList,HistoryList}){
            VirtualizingStackPanel.SetIsVirtualizing(list,true);
            VirtualizingStackPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(list,true);
            ScrollViewer.SetIsDeferredScrollingEnabled(list,true);
        }
    }
    private void RefreshLibrary() {
        if(refreshingLibrary)return;
        refreshingLibrary=true;
        try {
            var term=SearchBox.Text.Trim().ToUpperInvariant();
            var matches=term.Length==0?library.ToArray():library.Where(t=>t.SearchText.Contains(term,StringComparison.Ordinal)).ToArray();
            var selectedPath=(LibraryList.SelectedItem as Track)?.Path;
            // One CollectionChanged.Reset notification instead of thousands of add events.
            var refreshWatch=Stopwatch.StartNew();
            filtered.ReplaceAll(matches);
            filteredMusic.ReplaceAll(matches.Where(t=>GetMediaCategory(t.Path)=="MUSIC").ToArray());
            filteredJingles.ReplaceAll(matches.Where(t=>GetMediaCategory(t.Path)=="JINGLES"||GetMediaCategory(t.Path)=="SPOTS").ToArray());
            if(refreshWatch.ElapsedMilliseconds>150)LogDiagnostic("LIBRARY",$"Refresh {matches.Length:N0} tracks: {refreshWatch.ElapsedMilliseconds} ms");
            if(selectedPath!=null){
                var selected=matches.FirstOrDefault(t=>string.Equals(t.Path,selectedPath,StringComparison.OrdinalIgnoreCase));
                if(selected!=null)LibraryList.SelectedItem=selected;
            }
        } finally {refreshingLibrary=false;}
    }
    private void Import_Click(object s,RoutedEventArgs e) {
        var d=new OpenFileDialog { Filter="Audio files|*.mp3;*.wav;*.wma;*.aac;*.m4a;*.flac|All files|*.*", Multiselect=true };
        if(d.ShowDialog()!=true)return;
        int added=0;
        foreach(var p in d.FileNames)if(libraryPaths.Add(p)){library.Add(new Track{Path=p});added++;}
        RefreshLibrary();UpdateNext();SaveState();StatusText.Text=$"LIBRARY: {library.Count} (+{added})";
    }
    private async void ImportFolder_Click(object s,RoutedEventArgs e) {
        if(importing)return;
        var dialog=new OpenFolderDialog { Title="Επιλογή φακέλου μουσικής", Multiselect=false };
        if(dialog.ShowDialog()!=true)return;
        importing=true;importCancellation=new CancellationTokenSource();var token=importCancellation.Token; var importWatch=Stopwatch.StartNew();LogDiagnostic("IMPORT","Folder scan started");StatusText.Text="SCANNING FOLDER...";
        try {
            string root=dialog.FolderName;
            var found=await Task.Run(()=> {
                Thread.CurrentThread.Priority=ThreadPriority.BelowNormal;
                var paths=new List<string>(); var pending=new Stack<string>();pending.Push(root);
                while(pending.Count>0){
                    token.ThrowIfCancellationRequested();
                    var folder=pending.Pop();
                    try {
                        foreach(var p in Directory.EnumerateFiles(folder)){
                            token.ThrowIfCancellationRequested();
                            if(audioExtensions.Contains(System.IO.Path.GetExtension(p),StringComparer.OrdinalIgnoreCase))paths.Add(p);
                        }
                        foreach(var sub in Directory.EnumerateDirectories(folder))pending.Push(sub);
                    } catch(UnauthorizedAccessException){} catch(IOException){}
                }
                return paths;
            });
            int added=0;
            const int batchSize=250;
            for(int start=0;start<found.Count;start+=batchSize){
                token.ThrowIfCancellationRequested();
                int end=Math.Min(start+batchSize,found.Count);
                for(int i=start;i<end;i++){
                    var p=found[i];
                    if(libraryPaths.Add(p)){library.Add(new Track{Path=p});added++;}
                }
                StatusText.Text=$"IMPORT: {end:N0}/{found.Count:N0} αρχεία · {added:N0} νέα";
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
            }
            RefreshLibrary();UpdateNext();SaveState();StatusText.Text=$"FOLDER IMPORT: {added:N0} νέα τραγούδια";LogDiagnostic("IMPORT",$"{found.Count:N0} scanned, {added:N0} added in {importWatch.ElapsedMilliseconds} ms");
        } catch(OperationCanceledException){StatusText.Text="IMPORT CANCELLED";LogDiagnostic("IMPORT","Cancelled by user; already imported tracks retained");RefreshLibrary();SaveState();}
        catch(Exception ex){StatusText.Text="IMPORT ERROR: "+ex.Message;LogDiagnostic("ERROR","Import: "+ex);}
        finally{importing=false;importCancellation?.Dispose();importCancellation=null;}
    }
    private void ExportPlaylist_Click(object s,RoutedEventArgs e) {
        var d=new SaveFileDialog { Filter="M3U playlist|*.m3u", FileName="KYMA_Playlist.m3u" };
        if(d.ShowDialog()!=true)return;
        try { File.WriteAllLines(d.FileName,new[]{"#EXTM3U"}.Concat(queue.Select(t=>t.Path)),System.Text.Encoding.UTF8); StatusText.Text="PLAYLIST EXPORTED"; }
        catch(Exception ex){ StatusText.Text="EXPORT ERROR: "+ex.Message; }
    }
    private void ImportPlaylist_Click(object s,RoutedEventArgs e) {
        var d=new OpenFileDialog { Filter="M3U playlist|*.m3u;*.m3u8" };
        if(d.ShowDialog()!=true)return;
        try {
            int added=0; var root=System.IO.Path.GetDirectoryName(d.FileName)!;
            foreach(var line in File.ReadLines(d.FileName)) {
                var value=line.Trim(); if(value.Length==0 || value.StartsWith('#'))continue;
                var p=System.IO.Path.IsPathRooted(value)?value:System.IO.Path.GetFullPath(System.IO.Path.Combine(root,value));
                if(!File.Exists(p))continue;
                var t=library.FirstOrDefault(x=>string.Equals(x.Path,p,StringComparison.OrdinalIgnoreCase));
                if(t==null){t=new Track {Path=p}; library.Add(t); libraryPaths.Add(p);}
                if(preventQueueDuplicates&&queue.Any(x=>string.Equals(x.Path,p,StringComparison.OrdinalIgnoreCase)))continue;
                queue.Add(t); added++;
            }
            RefreshLibrary(); UpdateNext(); SaveState(); StatusText.Text=$"M3U IMPORT: {added} τραγούδια";
        } catch(Exception ex){ StatusText.Text="M3U ERROR: "+ex.Message; }
    }
    private void AddToQueue_Click(object s,RoutedEventArgs e) {
        if((DualLibraryPanel.Visibility==Visibility.Visible ? LibraryListA.SelectedItem ?? LibraryListB.SelectedItem : LibraryList.SelectedItem) is not Track t)return;
        if(preventQueueDuplicates&&queue.Any(x=>string.Equals(x.Path,t.Path,StringComparison.OrdinalIgnoreCase))) {
            StatusText.Text="TRACK ALREADY IN PLAYLIST";return;
        }
        queue.Add(t);UpdateNext();SaveState();
    }
    private void Duplicates_Changed(object s,RoutedEventArgs e) {
        preventQueueDuplicates=PreventDuplicatesCheck.IsChecked==true;
        if(IsLoaded)SaveState();
    }
    private void LibraryList_DoubleClick(object s,MouseButtonEventArgs e) => AddToQueue_Click(s,new RoutedEventArgs());
    private void SearchBox_TextChanged(object s,TextChangedEventArgs e) { if(searchDebounce==null)return; searchDebounce.Stop(); searchDebounce.Start(); }
    private void Play_Click(object s,RoutedEventArgs e) { if(!stopped) { player.Play();if(fading)incomingPlayer.Play(); paused=false; StatusText.Text="PLAYING"; return; } if(queue.Count>0) { int i=activeIndex>=0?activeIndex:0; while(i<queue.Count && !File.Exists(queue[i].Path))i++; if(i<queue.Count)StartAt(i); else StatusText.Text="NO AVAILABLE AUDIO FILES"; } }
    private void Pause_Click(object s,RoutedEventArgs e) {
        if(stopped)return;
        if(paused){player.Play();if(fading){incomingPlayer.Play();lastFadeClock=DateTime.UtcNow;}paused=false;StatusText.Text="PLAYING";}
        else {player.Pause();if(fading)incomingPlayer.Pause();paused=true;StatusText.Text="PAUSED";}
    }
    private void Stop_Click(object s,RoutedEventArgs e) => StopPlayback();
    private void StopPlayback() { incomingPlayer.Stop(); incomingPlayer.Close(); preloadedPath=null;preloadReady=false;fading=false;fadeElapsedSeconds=0; player.Volume=playbackVolume; player.Stop(); stopped=true; paused=false; PlaybackProgress.Value=0; NowTime.Text="00:00"; StatusText.Text="STOPPED"; }
    private void Next_Click(object s,RoutedEventArgs e) => NextTrack();
    private void MediaOpenedDiagnostic(object? sender,EventArgs e) {
        if(sender is not MediaPlayer source)return;
        var role=ReferenceEquals(source,player)?"MAIN":"STANDBY";
        if(ReferenceEquals(source,player))consecutiveAudioFailures=0;
        if(ReferenceEquals(source,incomingPlayer))standbyRetryCount=0;
        if(openRequests.Remove(source,out var request)){
            double elapsed=(DateTime.UtcNow-request.started).TotalMilliseconds;
            LogDiagnostic("AUDIO",$"{role} opened {System.IO.Path.GetFileName(request.path)} in {elapsed:F0} ms");
            if(elapsed>3000){
                slowOpenTotal++;
                slowOpenCounts[request.path]=slowOpenCounts.TryGetValue(request.path,out var n)?n+1:1;
                LogDiagnostic("PRELOAD",$"SLOW OPEN: {role} · {System.IO.Path.GetFileName(request.path)} · {elapsed:F0} ms");
                DiagnoseSlowNasFile(request.path,elapsed);
            }
        }
        else LogDiagnostic("AUDIO",$"{role} MediaOpened (no pending request)");
    }
    private void Player_MediaOpened(object? sender,EventArgs e) {
        Dispatcher.BeginInvoke(() => {
            if(ReferenceEquals(sender,player)&&player.NaturalDuration.HasTimeSpan){
                PlaybackProgress.Maximum=Math.Max(1,player.NaturalDuration.TimeSpan.TotalSeconds);
                UpdateFadeCountdown();
            }
            if(ReferenceEquals(sender,incomingPlayer)&&preloadedPath!=null){
                preloadReady=true;preloadWarningPath=null;
                double readyMs=(DateTime.UtcNow-preloadStartedAt).TotalMilliseconds;
                preloadReadyCount++;preloadTotalMs+=readyMs;preloadMaxMs=Math.Max(preloadMaxMs,readyMs);
                LogDiagnostic("PRELOAD",$"READY {System.IO.Path.GetFileName(preloadedPath)} · elapsed={readyMs:F0} ms");
            }
        });
    }
    private void Player_MediaEnded(object? sender,EventArgs e) {
        Dispatcher.BeginInvoke(() => {
            if(!ReferenceEquals(sender,player)||fading)return;
            if(auto)NextTrack();else StopPlayback();
        });
    }
    private int FindUpcomingIndex() {
        int next=activeIndex+1;
        while(next<queue.Count&&!File.Exists(queue[next].Path)){
            skippedMissingFiles++;
            if((DateTime.UtcNow-lastMissingLogUtc).TotalSeconds>=30){
                lastMissingLogUtc=DateTime.UtcNow;
                LogDiagnostic("QUEUE",$"Skipping missing file: {System.IO.Path.GetFileName(queue[next].Path)} · total skipped checks={skippedMissingFiles}");
            }
            next++;
        }
        if(next>=queue.Count&&repeatPlaylist){
            next=0;
            while(next<queue.Count&&!File.Exists(queue[next].Path)){
                skippedMissingFiles++;
                next++;
            }
        }
        return next<queue.Count&&next!=activeIndex?next:-1;
    }
    private void PreloadUpcoming() {
        if(fading)return;
        int next=FindUpcomingIndex();
        string? path=next>=0?queue[next].Path:null;
        if(path==preloadedPath)return;
        preloadedPath=null;preloadReady=false;preloadWarningPath=null;
        try {
            incomingPlayer.Stop();incomingPlayer.Close();
            if(path==null)return;
            incomingPlayer.Volume=0;
            TrackMediaOpen(incomingPlayer,path,"STANDBY");
            preloadedPath=path;preloadStartedAt=DateTime.UtcNow;
            incomingPlayer.Open(new Uri(path));
        } catch(Exception ex){preloadedPath=null;StatusText.Text="PRELOAD ERROR: "+ex.Message;}
    }
    private void BeginCrossfade() {
        if(fading||stopped||!auto)return;
        int next=FindUpcomingIndex();
        if(next<0)return;
        if(preloadedPath!=queue[next].Path){PreloadUpcoming();return;}
        if(!preloadReady){
            if((DateTime.UtcNow-preloadStartedAt).TotalSeconds>3)StatusText.Text="WAITING FOR NEXT AUDIO";
            CheckPreloadHealth();
            return;
        }
        try {
            incomingPlayer.Volume=0;incomingPlayer.Play();outgoingFadeEnd=EffectiveEndSeconds();
            fadeStartPosition=player.Position.TotalSeconds;
            outgoingFadeDuration=Math.Max(0.1,outgoingFadeEnd-fadeStartPosition);
            if(outgoingFadeDuration<0.5){
                shortJingleTransitions++;
                LogDiagnostic("CROSSFADE",$"SHORT TRACK #{shortJingleTransitions}: reduced fade to {outgoingFadeDuration:F2}s for {System.IO.Path.GetFileName(queue[activeIndex].Path)}");
            }
            fadeElapsedSeconds=0;lastFadeClock=DateTime.UtcNow;
            crossfadeStartedUtc=DateTime.UtcNow;
            crossfadeFrom=activeIndex>=0&&activeIndex<queue.Count?queue[activeIndex].Display:"(unknown)";
            crossfadeTo=queue[next].Display;
            crossfadeStartRamMb=MemoryMb();
            LogDiagnostic("CROSSFADE",$"START {crossfadeFrom} -> {crossfadeTo} · out={fadeStartPosition:F2}s · target={outgoingFadeDuration:F2}s · RAM={crossfadeStartRamMb} MB");
            fading=true;
            activeIndex=next;NowTitle.Text=queue[next].Display;NowSub.Text=queue[next].Path;
            QueueList.SelectedIndex=next;history.Insert(0,$"{DateTime.Now:HH:mm:ss}   {queue[next].Display}");
            if(history.Count>100)history.RemoveAt(history.Count-1);
            UpdateNext();pendingStateSave=true;StatusText.Text="CROSSFADE";
        } catch(Exception ex){fading=false;StatusText.Text="CROSSFADE ERROR: "+ex.Message;}
    }
    private void ApplyFadeVolumes() {
        double progress=Math.Clamp(fadeElapsedSeconds/outgoingFadeDuration,0,1);
        player.Volume=playbackVolume*Math.Cos(progress*Math.PI/2);
        incomingPlayer.Volume=playbackVolume*Math.Sin(progress*Math.PI/2);
    }
    private void FinishCrossfade() {
        if(!fading)return;
        completedCrossfades++;
        long finishRamMb=MemoryMb();
        double actualSeconds=crossfadeStartedUtc==DateTime.MinValue?0:(DateTime.UtcNow-crossfadeStartedUtc).TotalSeconds;
        LogDiagnostic("CROSSFADE",$"END #{completedCrossfades} {crossfadeFrom} -> {crossfadeTo} · actual={actualSeconds:F2}s · target={outgoingFadeDuration:F2}s · RAM={finishRamMb} MB · delta={finishRamMb-crossfadeStartRamMb:+#;-#;0} MB");
        var outgoing=player;player=incomingPlayer;incomingPlayer=outgoing;
        incomingPlayer.Stop();incomingPlayer.Close();incomingPlayer.Volume=playbackVolume;
        player.Volume=playbackVolume;fading=false;fadeElapsedSeconds=0;preloadedPath=null;preloadReady=false;PlaybackProgress.Value=0;if(player.NaturalDuration.HasTimeSpan)PlaybackProgress.Maximum=Math.Max(1,player.NaturalDuration.TimeSpan.TotalSeconds);
        DrawWave(NowWaveform,activeIndex>=0&&activeIndex<queue.Count?queue[activeIndex].Path:null);
        StatusText.Text="PLAYING";UpdateNext();
    }
    private void NextTrack() {
        if(movingNext)return; movingNext=true;
        try {
            if(queue.Count==0) { StopPlayback(); return; }
            int next=activeIndex+1;
            while(next<queue.Count && !File.Exists(queue[next].Path))next++;
            if(next>=queue.Count && repeatPlaylist) { next=0; while(next<queue.Count&&!File.Exists(queue[next].Path))next++; }
            if(next>=queue.Count) { StopPlayback(); StatusText.Text="END OF PLAYLIST"; return; }
            StartAt(next);
        } finally { movingNext=false; }
    }
    private void Repeat_Click(object s,RoutedEventArgs e) {
        repeatPlaylist=!repeatPlaylist; RefreshRepeatButton(); UpdateNext(); SaveState();
    }
    private void RefreshRepeatButton() {
        RepeatButton.Content=repeatPlaylist?"REPEAT ON":"REPEAT OFF";
        RepeatButton.Background=new SolidColorBrush(repeatPlaylist?Color.FromRgb(25,118,52):Color.FromRgb(70,74,90));
    }
    private bool cleaningLibrary = false;
    private async void RemoveMissing_Click(object s,RoutedEventArgs e) {
        if(cleaningLibrary)return;
        cleaningLibrary=true;cleanCancellation=new CancellationTokenSource();var token=cleanCancellation.Token;
        var watch=Stopwatch.StartNew();
        LogDiagnostic("CLEAN","Fast folder-based scan started");
        StatusText.Text="CLEAN: γρήγορος έλεγχος φακέλων...";
        try {
            var paths=library.Select(t=>t.Path).Concat(queue.Select(t=>t.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var folderGroups=paths.GroupBy(p=>System.IO.Path.GetDirectoryName(p)??"",
                StringComparer.OrdinalIgnoreCase).ToArray();
            IProgress<(int count,int total,int missing,int skipped)> progress=
                new Progress<(int count,int total,int missing,int skipped)>(p=>{
                    StatusText.Text=$"CLEAN: {p.count:N0}/{p.total:N0} φάκελοι · {watch.Elapsed:mm\\:ss}";
                    if(p.count%100==0||p.count==p.total)
                        LogDiagnostic("CLEAN",$"Folders {p.count:N0}/{p.total:N0}, missing={p.missing:N0}, inaccessible={p.skipped:N0}, elapsed={watch.Elapsed:mm\\:ss}");
                });
            var result=await Task.Run(()=>{
                Thread.CurrentThread.Priority=ThreadPriority.BelowNormal;
                var missing=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int skipped=0;
                int processed=0;
                foreach(var group in folderGroups){
                    token.ThrowIfCancellationRequested();
                    try {
                        // Enumerate only the immediate directory, not its entire subtree.
                        // If enumeration fails, preserve every library entry in that folder.
                        var existing=new HashSet<string>(
                            Directory.EnumerateFiles(group.Key),StringComparer.OrdinalIgnoreCase);
                        foreach(var path in group)
                            if(!existing.Contains(path))missing.Add(path);
                    } catch(Exception ex) when(ex is IOException||ex is UnauthorizedAccessException||
                        ex is ArgumentException||ex is NotSupportedException||ex is System.Security.SecurityException){
                        skipped++;
                    }
                    processed++;
                    if(!stopped && processed%8==0)Thread.Sleep(2);
                    if(processed%25==0||processed==folderGroups.Length)
                        progress.Report((processed,folderGroups.Length,missing.Count,skipped));
                }
                return (missing,skipped);
            });
            token.ThrowIfCancellationRequested();
            LogDiagnostic("CLEAN",$"Folder scan finished in {watch.ElapsedMilliseconds} ms; {folderGroups.Length:N0} folders; {result.missing.Count:N0} missing; {result.skipped:N0} inaccessible");
            var kept=library.Where(t=>!result.missing.Contains(t.Path)).ToArray();
            int removed=library.Count-kept.Length;
            if(removed>0){
                library.Clear();
                foreach(var track in kept)library.Add(track);
                libraryPaths.Clear();
                foreach(var track in kept)libraryPaths.Add(track.Path);
            }
            for(int i=queue.Count-1;i>=0;i--)
                if(result.missing.Contains(queue[i].Path)){
                    if(i==activeIndex){StopPlayback();activeIndex=-1;}
                    else if(i<activeIndex)activeIndex--;
                    queue.RemoveAt(i);
                }
            if(removed>0)RefreshLibrary();
            UpdateNext();
            if(result.missing.Count>0)SaveState();
            StatusText.Text=$"CLEAN OK: {removed:N0} αφαιρέθηκαν · {folderGroups.Length:N0} φάκελοι · {result.skipped:N0} μη προσβάσιμοι";
            LogDiagnostic("CLEAN",$"Completed in {watch.Elapsed:mm\\:ss}; {paths.Length:N0} entries; removed={removed:N0}; inaccessible folders={result.skipped:N0}");
        } catch(OperationCanceledException){StatusText.Text="CLEAN CANCELLED · no entries removed";LogDiagnostic("CLEAN","Cancelled; library unchanged");}
        catch(Exception ex){StatusText.Text="CLEAN ERROR: "+ex.Message;LogDiagnostic("ERROR","Clean: "+ex);}
        finally{cleaningLibrary=false;cleanCancellation?.Dispose();cleanCancellation=null;}
    }
    private void ClearHistory_Click(object s,RoutedEventArgs e) {
        if(history.Count==0)return;
        if(MessageBox.Show("Να διαγραφεί το ιστορικό αναπαραγωγής;","KYMA",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        history.Clear();SaveState();StatusText.Text="HISTORY CLEARED";
    }
    private void ExportHistory_Click(object s,RoutedEventArgs e) {
        var d=new SaveFileDialog{Filter="CSV file|*.csv",FileName="KYMA_History.csv"};
        if(d.ShowDialog()!=true)return;
        try{File.WriteAllLines(d.FileName,new[]{"Entry"}.Concat(history.Select(h=>"\""+h.Replace("\"","\"\"")+"\"")),System.Text.Encoding.UTF8);StatusText.Text="HISTORY EXPORTED";}
        catch(Exception ex){StatusText.Text="EXPORT ERROR: "+ex.Message;}
    }
    private void StartAt(int index) { LogDiagnostic("AUDIO",$"StartAt index={index} queue={queue.Count}"); if(fading){incomingPlayer.Stop();incomingPlayer.Close();fading=false;fadeElapsedSeconds=0;player.Volume=playbackVolume;}preloadedPath=null;preloadReady=false; if(index<0||index>=queue.Count)return; var t=queue[index]; if(!File.Exists(t.Path)) { StatusText.Text="MISSING FILE: "+t.Path; return; } player.Stop();TrackMediaOpen(player,t.Path,"MAIN");player.Open(new Uri(t.Path));player.Play();activeIndex=index; stopped=false; paused=false; NowTitle.Text=t.Display; NowSub.Text=t.Path; QueueList.SelectedIndex=index; history.Insert(0,$"{DateTime.Now:HH:mm:ss}   {t.Display}"); if(history.Count>100)history.RemoveAt(history.Count-1); SaveState(); StatusText.Text="PLAYING"; PlaybackProgress.Value=0; UpdateNext(); DrawWave(NowWaveform,t.Path); }
    private void ConfigureAudioMenus() {
        var queueMenu=new ContextMenu();
        var setOut=new MenuItem{Header="Mix Out → τρέχουσα θέση αναπαραγωγής"};
        setOut.Click+=(_,_)=>{
            if(activeIndex<0||activeIndex>=queue.Count||!player.NaturalDuration.HasTimeSpan)return;
            if(fading){StatusText.Text="MIX OUT: WAIT UNTIL FADE ENDS";return;}
            manualMixOut[queue[activeIndex].Path]=Math.Clamp(player.Position.TotalSeconds,1,player.NaturalDuration.TimeSpan.TotalSeconds);
            SaveState();StatusText.Text="MIX OUT SAVED";UpdateFadeCountdown();
        };
        var clearOut=new MenuItem{Header="Καθαρισμός Mix Out επιλεγμένου τραγουδιού"};
        clearOut.Click+=(_,_)=>{if(QueueList.SelectedItem is Track t){manualMixOut.Remove(t.Path);SaveState();StatusText.Text="MIX OUT RESET";}};
        queueMenu.Items.Add(setOut);queueMenu.Items.Add(clearOut);QueueList.ContextMenu=queueMenu;
        var settings=new ContextMenu();
        foreach(double db in new[]{-54d,-48d,-42d,-36d,-30d}){
            double choice=db;
            var item=new MenuItem{Header=$"Silence Detection {db:0} dB",IsCheckable=true,IsChecked=db==silenceThresholdDb};
            item.Click+=(_,_)=>{
                silenceThresholdDb=choice;
                foreach(MenuItem other in settings.Items)other.IsChecked=ReferenceEquals(other,item);
                analysisGeneration++;audibleEnds.Clear();waveCache.Clear();decodedDurations.Clear();
                if(activeIndex>=0&&activeIndex<queue.Count)DrawWave(NowWaveform,queue[activeIndex].Path);
                UpdateNext();SaveState();StatusText.Text=$"SILENCE THRESHOLD {choice:0} dB";
            };
            settings.Items.Add(item);
        }
        FadeCountdown.ContextMenu=settings;
        FadeCountdown.ToolTip="Δεξί κλικ: ρύθμιση ορίου σιωπής";
    }
    private void UpdateFadeCountdown() {
        if(stopped){FadeCountdown.Text="ΑΝΑΜΟΝΗ";return;}
        if(paused){FadeCountdown.Text="ΠΑΥΣΗ";return;}
        if(!auto){FadeCountdown.Text="MANUAL · ΧΩΡΙΣ ΑΥΤΟΜΑΤΗ ΜΙΞΗ";return;}
        if(crossfadeSeconds<=0){FadeCountdown.Text="CROSSFADE OFF";return;}
        if(fading){FadeCountdown.Text="MIXING · ΜΙΞΗ ΣΕ ΕΞΕΛΙΞΗ";return;}
        if(CurrentDurationSeconds()<=0){FadeCountdown.Text="ΥΠΟΛΟΓΙΣΜΟΣ ΧΡΟΝΟΥ...";return;}
        int next=activeIndex+1;
        while(next<queue.Count&&!File.Exists(queue[next].Path))next++;
        if(next>=queue.Count&&repeatPlaylist){next=0;while(next<queue.Count&&!File.Exists(queue[next].Path))next++;}
        if(next>=queue.Count||next==activeIndex){FadeCountdown.Text="ΔΕΝ ΥΠΑΡΧΕΙ ΕΠΟΜΕΝΟ ΤΡΑΓΟΥΔΙ";return;}
        double seconds=Math.Max(0,EffectiveEndSeconds()-player.Position.TotalSeconds-crossfadeSeconds);
        var until=TimeSpan.FromSeconds(Math.Ceiling(seconds));
        FadeCountdown.Text=$"ΕΝΑΡΞΗ ΜΙΞΗΣ ΣΕ {until.Minutes:00}:{until.Seconds:00}";
    }
    private void UpdateNext() {
        int next=activeIndex<0?0:activeIndex+1;
        if(next>=queue.Count && repeatPlaylist)next=0;
        string? nextPath=next>=0&&next<queue.Count?queue[next].Path:null;
        NextTitle.Text=nextPath!=null?queue[next].Display:"—";
        QueueCount.Text=$"Playlist: {queue.Count}  |  Library: {library.Count}";
        DrawWave(NextWaveform,nextPath);
        PreloadUpcoming();
    }
    private void QueueList_SelectionChanged(object s,SelectionChangedEventArgs e) { }
    private void ClearQueue_Click(object s,RoutedEventArgs e) {
        if(queue.Count==0)return;
        if(MessageBox.Show("Να διαγραφεί ολόκληρη η playlist;","KYMA",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        StopPlayback(); activeIndex=-1; queue.Clear(); NowTitle.Text="Δεν αναπαράγεται τραγούδι"; NowSub.Text=""; DrawWave(NowWaveform,null); UpdateNext(); SaveState(); StatusText.Text="PLAYLIST CLEARED";
    }
    private void ShuffleUpcoming_Click(object s,RoutedEventArgs e) {
        int start=Math.Max(0,activeIndex+1);
        if(queue.Count-start<2)return;
        var tracks=queue.Skip(start).ToList();
        for(int i=tracks.Count-1;i>0;i--){int j=random.Next(i+1);(tracks[i],tracks[j])=(tracks[j],tracks[i]);}
        for(int i=0;i<tracks.Count;i++)queue[start+i]=tracks[i];
        UpdateNext(); SaveState(); StatusText.Text="UPCOMING TRACKS SHUFFLED";
    }
    private void Crossfade_Changed(object s,RoutedPropertyChangedEventArgs<double> e) {
        crossfadeSeconds=e.NewValue;
        if(CrossfadeValue!=null)CrossfadeValue.Text=$"{crossfadeSeconds:0} s";
        if(IsLoaded)SaveState();
    }
    private void Volume_Changed(object s,RoutedPropertyChangedEventArgs<double> e) {
        playbackVolume=e.NewValue/100.0; if(fading)ApplyFadeVolumes();else{player.Volume=playbackVolume;incomingPlayer.Volume=playbackVolume;} foreach(var cp in cartPlayers)cp.Volume=playbackVolume;
        if(IsLoaded)SaveState();
    }
    private void PlaybackProgress_MouseDown(object s,MouseButtonEventArgs e) {
        if(stopped || !player.NaturalDuration.HasTimeSpan || PlaybackProgress.ActualWidth<=0)return;
        double fraction=Math.Clamp(e.GetPosition(PlaybackProgress).X/PlaybackProgress.ActualWidth,0,1);
        player.Position=TimeSpan.FromSeconds(player.NaturalDuration.TimeSpan.TotalSeconds*fraction);
        PlaybackProgress.Value=player.Position.TotalSeconds;
    }
    private void PlaySelected_Click(object s,RoutedEventArgs e) { if(QueueList.SelectedIndex>=0) StartAt(QueueList.SelectedIndex); }
    private void MoveUp_Click(object s,RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object s,RoutedEventArgs e) => Move(1);
    private void Move(int delta) { int i=QueueList.SelectedIndex,j=i+delta; if(i<0||j<0||j>=queue.Count)return; queue.Move(i,j); if(activeIndex==i)activeIndex=j; else if(activeIndex==j)activeIndex=i; QueueList.SelectedIndex=j; UpdateNext(); SaveState(); }
    private void Remove_Click(object s,RoutedEventArgs e) { int i=QueueList.SelectedIndex; if(i<0)return; if(i==activeIndex) { StopPlayback(); activeIndex=-1; NowTitle.Text="Δεν αναπαράγεται τραγούδι"; NowSub.Text=""; } else if(i<activeIndex)activeIndex--; queue.RemoveAt(i); UpdateNext(); SaveState(); }
    private void Auto_Click(object s,RoutedEventArgs e) { auto=!auto; RefreshAutoButton(); SaveState(); }
    private void RefreshAutoButton() { AutoButton.Content=auto?"AUTO":"MANUAL"; AutoButton.Background=new SolidColorBrush(auto?Color.FromRgb(25,118,52):Color.FromRgb(138,74,51)); }
    private void SingleLayout_Click(object s,RoutedEventArgs e) { DualLibraryPanel.Visibility=Visibility.Collapsed; LibraryList.Visibility=Visibility.Visible; StatusText.Text="SINGLE LIBRARY LAYOUT"; }
    private void DoubleLayout_Click(object s,RoutedEventArgs e) { LibraryList.Visibility=Visibility.Collapsed; DualLibraryPanel.Visibility=Visibility.Visible; StatusText.Text="DUAL LIBRARY VIEW — κοινή βάση, ανεξάρτητες επιλογές"; }
    private void LibraryA_DoubleClick(object s,MouseButtonEventArgs e) { if(LibraryListA.SelectedItem is Track t) { queue.Add(t); UpdateNext(); SaveState(); } }
    private void LibraryB_DoubleClick(object s,MouseButtonEventArgs e) { if(LibraryListB.SelectedItem is Track t) { queue.Add(t); UpdateNext(); SaveState(); } }
    private void LoadCart_Click(object s,RoutedEventArgs e) { var d=new OpenFileDialog{Filter="Audio files|*.mp3;*.wav;*.wma;*.aac;*.m4a;*.flac|All files|*.*"}; if(d.ShowDialog()!=true)return; int n=Array.FindIndex(carts,x=>x==null); if(n<0)n=0; carts[n]=d.FileName; UpdateCartPad(n,false); SaveState(); }
    private void AssignCart(int n) { var d=new OpenFileDialog{Filter="Audio files|*.mp3;*.wav;*.wma;*.aac;*.m4a;*.flac|All files|*.*"}; if(d.ShowDialog()!=true)return; carts[n]=d.FileName; UpdateCartPad(n,false); SaveState(); StatusText.Text=$"CART {n+1} ASSIGNED"; }
    private void PlayCart(int n) {
        var p=carts[n]; if(string.IsNullOrEmpty(p)||!File.Exists(p)) { StatusText.Text="LOAD CART FIRST"; return; }
        cartPlayers[n].Stop(); cartPlayers[n].Open(new Uri(p)); cartPlayers[n].Play();
        UpdateCartPad(n,true); StatusText.Text=$"CART {n+1}: "+System.IO.Path.GetFileName(p);
    }
    private void StopSingleCart(int n) {
        cartPlayers[n].Stop();UpdateCartPad(n,false);StatusText.Text=$"CART {n+1} STOPPED";
    }
    private void UpdateCartPad(int n,bool playing) {
        if(n<0||n>=CartGrid.Children.Count)return;
        var b=(Button)CartGrid.Children[n];
        var title=carts[n] is string p ? System.IO.Path.GetFileNameWithoutExtension(p).Replace('_',' ') : $"CART {n+1}";
        b.Content=new TextBlock { Text=title, TextWrapping=TextWrapping.Wrap, TextAlignment=TextAlignment.Center, TextTrimming=TextTrimming.CharacterEllipsis, MaxHeight=56, FontSize=12 };
        b.Padding=new Thickness(4);
        b.Background=new SolidColorBrush(playing?Color.FromRgb(20,137,91):carts[n]!=null?Color.FromRgb(55,78,119):Color.FromRgb(54,59,75));
        b.ToolTip=$"CART {n+1}: {carts[n] ?? "Κενό"}\nΚλικ/πλήκτρο {n+1}: PLAY\nShift+κλικ: STOP μόνο αυτό το Cart\nΔεξί κλικ: αλλαγή αρχείου";
    }
    private void ClearCart_Click(object s,RoutedEventArgs e) { if(CartGrid.Children.Count==0)return; if(MessageBox.Show("Να καθαριστούν και τα 9 Cart;","KYMA",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return; for(int i=0;i<9;i++){cartPlayers[i].Stop();carts[i]=null;UpdateCartPad(i,false);}SaveState();StatusText.Text="CARTWALL CLEARED"; }
    private void StopCart_Click(object s,RoutedEventArgs e) { for(int i=0;i<cartPlayers.Length;i++){cartPlayers[i].Stop();UpdateCartPad(i,false);} StatusText.Text="ALL CARTS STOPPED"; }
    private void Save_Click(object s,RoutedEventArgs e) { SaveState(); StatusText.Text="LIBRARY SAVED"; }
    private sealed class SavedState { public List<string> Library {get;set;}=new(); public List<string> Queue {get;set;}=new(); public string?[] Carts {get;set;}=new string?[9]; public bool Auto {get;set;}=false; public bool Repeat {get;set;}=false; public double Crossfade {get;set;}=5; public bool PreventDuplicates {get;set;}=true; public double SilenceThresholdDb {get;set;}=-42; public Dictionary<string,double> MixOut {get;set;}=new(); public double Volume {get;set;}=85; public List<string> History {get;set;}=new(); }
    private void SaveState() { if(loadingState)return;stateDirty=true;saveDebounce.Stop();saveDebounce.Start(); }
    private void FlushState() { if(!stateDirty)return; var saveWatch=Stopwatch.StartNew();try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stateFile)!); File.WriteAllText(stateFile,JsonSerializer.Serialize(new SavedState { Library=library.Select(t=>t.Path).ToList(),Queue=queue.Select(t=>t.Path).ToList(),Carts=carts,Auto=auto,Repeat=repeatPlaylist,Crossfade=crossfadeSeconds,PreventDuplicates=preventQueueDuplicates,SilenceThresholdDb=silenceThresholdDb,MixOut=new Dictionary<string,double>(manualMixOut),Volume=playbackVolume*100,History=history.ToList() })); stateDirty=false;LogDiagnostic("SAVE",$"State saved in {saveWatch.ElapsedMilliseconds} ms"); } catch(Exception ex) { StatusText.Text="SAVE ERROR: "+ex.Message;LogDiagnostic("ERROR","Save: "+ex); } }
    private void LoadState() { loadingState=true;try { if(!File.Exists(stateFile))return; var state=JsonSerializer.Deserialize<SavedState>(File.ReadAllText(stateFile)); if(state==null)return; silenceThresholdDb=Math.Clamp(state.SilenceThresholdDb,-54,-30); manualMixOut.Clear();foreach(var entry in state.MixOut??new Dictionary<string,double>())if(entry.Value>0)manualMixOut[entry.Key]=entry.Value; auto=state.Auto; RefreshAutoButton(); repeatPlaylist=state.Repeat; RefreshRepeatButton(); crossfadeSeconds=Math.Clamp(state.Crossfade,0,15);CrossfadeSlider.Value=crossfadeSeconds;preventQueueDuplicates=state.PreventDuplicates;PreventDuplicatesCheck.IsChecked=preventQueueDuplicates; playbackVolume=Math.Clamp(state.Volume/100.0,0,1); player.Volume=playbackVolume; foreach(var cp in cartPlayers)cp.Volume=playbackVolume; VolumeSlider.Value=playbackVolume*100; foreach(var entry in state.History??new List<string>()) { if(history.Count>=100)break; history.Add(entry); } foreach(var p in state.Library)if(!string.IsNullOrWhiteSpace(p)&&libraryPaths.Add(p))library.Add(new Track{Path=p}); foreach(var p in state.Queue)if(!string.IsNullOrWhiteSpace(p))queue.Add(new Track{Path=p}); for(int i=0;i<Math.Min(9,(state.Carts?.Length ?? 0));i++) { if(state.Carts![i] is string p && File.Exists(p)) { carts[i]=p; UpdateCartPad(i,false); } } RefreshLibrary(); UpdateNext(); } catch(Exception ex) { StatusText.Text="LOAD ERROR: "+ex.Message; } finally {loadingState=false;} }
    private void Window_KeyDown(object sender,KeyEventArgs e) { if(Keyboard.FocusedElement is TextBox)return; if(e.Key==Key.F5) { Play_Click(sender,new RoutedEventArgs()); e.Handled=true; } else if(e.Key==Key.F6) { Pause_Click(sender,new RoutedEventArgs()); e.Handled=true; } else if(e.Key==Key.F7) { Stop_Click(sender,new RoutedEventArgs()); e.Handled=true; } else if(e.Key==Key.F8) { Next_Click(sender,new RoutedEventArgs()); e.Handled=true; } else if(e.Key==Key.F9) { Auto_Click(sender,new RoutedEventArgs()); e.Handled=true; } else if(e.Key==Key.F10) { Repeat_Click(sender,new RoutedEventArgs()); e.Handled=true; } else if(e.Key>=Key.D1 && e.Key<=Key.D9) { PlayCart((int)e.Key-(int)Key.D1); e.Handled=true; } else if(e.Key>=Key.NumPad1 && e.Key<=Key.NumPad9) { PlayCart((int)e.Key-(int)Key.NumPad1); e.Handled=true; } }
    private void Waveform_SizeChanged(object sender,SizeChangedEventArgs e) {
        if(sender==NowWaveform)DrawWave(NowWaveform,activeIndex>=0&&activeIndex<queue.Count?queue[activeIndex].Path:null);
        else {int next=activeIndex<0?0:activeIndex+1;if(next>=queue.Count&&repeatPlaylist)next=0;DrawWave(NextWaveform,next>=0&&next<queue.Count?queue[next].Path:null);}
    }
    private readonly Dictionary<string,double[]> waveCache=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> waveBusy=new(StringComparer.OrdinalIgnoreCase);
    private async void DecodeWave(string path) {
        if(waveBusy.Contains(path)||waveCache.ContainsKey(path))return;
        waveBusy.Add(path);
        int generation=analysisGeneration;
        double detectionThreshold=silenceThresholdDb;
        bool workerAcquired=false;
        try {
            await waveformWorker.WaitAsync();
            workerAcquired=true;
            if(generation!=analysisGeneration)return;
            var result=await Task.Run(()=>{
                var values=new double[105];
                using var audio=new NAudio.Wave.AudioFileReader(path);
                // Detect the last sustained audible window; ignore isolated noise in the tail.
                double audibleEnd=audio.TotalTime.TotalSeconds;
                double lastLoudSecond=0;
                double windowSum=0;long windowCount=0;long windowStart=0;
                long windowSamples=Math.Max(1,audio.WaveFormat.SampleRate*audio.WaveFormat.Channels/10);
                double threshold=Math.Pow(10,detectionThreshold/20.0);
                long total=(long)(audio.TotalTime.TotalSeconds*audio.WaveFormat.SampleRate*audio.WaveFormat.Channels);
                var data=new float[4096];long pos=0;int n;
                while((n=audio.Read(data,0,data.Length))>0){
                    for(int k=0;k<n;k++){
                        int bin=(int)Math.Clamp((pos+k)*105/Math.Max(1,total),0,104);
                        double amplitude=Math.Abs(data[k]);
                        values[bin]=Math.Max(values[bin],amplitude);
                        windowSum+=data[k]*data[k];windowCount++;
                        if(windowCount>=windowSamples){
                            if(Math.Sqrt(windowSum/windowCount)>=threshold)lastLoudSecond=(pos+k+1)/(double)(audio.WaveFormat.SampleRate*audio.WaveFormat.Channels);
                            windowSum=0;windowCount=0;windowStart=pos+k+1;
                        }
                    }
                    pos+=n;
                }
                if(windowCount>0&&Math.Sqrt(windowSum/windowCount)>=threshold)lastLoudSecond=pos/(double)(audio.WaveFormat.SampleRate*audio.WaveFormat.Channels);
                if(lastLoudSecond>0.5&&audibleEnd-lastLoudSecond>=0.7)audibleEnd=lastLoudSecond;
                return (values,audibleEnd,duration:audio.TotalTime.TotalSeconds);
            });
            if(generation!=analysisGeneration)return;
            audibleEnds[path]=result.audibleEnd;
            decodedDurations[path]=result.duration;
            LogDiagnostic("WAVE",$"Decoded {System.IO.Path.GetFileName(path)} duration={result.duration:F1}s");
            waveCache[path]=result.values;if(waveCache.Count>64){var oldest=waveCache.Keys.First();waveCache.Remove(oldest);audibleEnds.Remove(oldest);decodedDurations.Remove(oldest);}
            DrawWave(NowWaveform,activeIndex>=0&&activeIndex<queue.Count?queue[activeIndex].Path:null);
            int next=activeIndex<0?0:activeIndex+1;if(next>=queue.Count&&repeatPlaylist)next=0;
            DrawWave(NextWaveform,next>=0&&next<queue.Count?queue[next].Path:null);
        }catch(Exception ex){waveCache[path]=new double[105];StatusText.Text="WAVE ERROR: "+ex.Message;}finally{waveBusy.Remove(path);if(workerAcquired)waveformWorker.Release();}
    }
    private void UpdateWavePlayhead() {
        if(stopped||!player.NaturalDuration.HasTimeSpan||NowWaveform.ActualWidth<10)return;
        double fraction=Math.Clamp(player.Position.TotalSeconds/Math.Max(0.01,player.NaturalDuration.TimeSpan.TotalSeconds),0,1);
        double x=fraction*NowWaveform.ActualWidth;
        Canvas.SetLeft(WavePlayhead,x);
    }
    private void DrawWave(Canvas canvas,string? path) {
        canvas.Children.Clear(); double width=canvas.ActualWidth; if(width<10)return;
        const int bars=105; var heights=new double[bars]; if(path!=null&&File.Exists(path)){if(waveCache.TryGetValue(path,out var peaks))Array.Copy(peaks,heights,bars);else if(!waveBusy.Contains(path))DecodeWave(path);}
        if(false) {
            try { using var f=File.OpenRead(path); using var r=new BinaryReader(f); if(new string(r.ReadChars(4))=="RIFF") { f.Position=8; if(new string(r.ReadChars(4))=="WAVE") { long dataStart=-1; int dataLength=0; int bits=0; int channels=0; while(f.Position+8<=f.Length) { var chunk=new string(r.ReadChars(4)); int size=r.ReadInt32(); long end=f.Position+size; if(size<0 || end>f.Length)break; if(chunk=="fmt " && size>=16) { int format=r.ReadInt16(); channels=r.ReadInt16(); r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits=r.ReadInt16(); if(format!=1)bits=0; } if(chunk=="data") { dataStart=f.Position; dataLength=size; } f.Position=end+(size%2); } if(dataStart>=0 && bits==16 && channels>0) { int frameSize=channels*2; for(int i=0;i<bars;i++) { long pos=dataStart+(long)((i+0.5)/bars*dataLength/frameSize)*frameSize; f.Position=Math.Min(dataStart+dataLength-frameSize, pos); double peak=0; for(int j=0;j<128 && f.Position+frameSize<=dataStart+dataLength;j++) { short sample=r.ReadInt16(); peak=Math.Max(peak,Math.Abs((double)sample/32768)); f.Position+=(channels-1)*2; } heights[i]=Math.Clamp(peak,0.04,1); } } } } } catch { }
        }
        for(int i=0;i<bars;i++) { double h=(heights[i]>0?heights[i]:0.04)*Math.Max(10,canvas.ActualHeight-6); var rect=new Rectangle { Width=Math.Max(1,width/bars-1),Height=h,Fill=new SolidColorBrush(i<bars/2?Color.FromRgb(43,191,218):Color.FromRgb(84,213,145)),Opacity=0.9 }; Canvas.SetLeft(rect,i*width/bars); Canvas.SetTop(rect,(canvas.ActualHeight-h)/2); canvas.Children.Add(rect); }
    }

}
