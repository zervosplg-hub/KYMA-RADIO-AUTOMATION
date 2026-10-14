using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
public sealed class Track { public string Path { get; set; } = ""; public string Display => System.IO.Path.GetFileNameWithoutExtension(Path); }
public partial class MainWindow : Window
{
    private readonly ObservableCollection<Track> library = new();
    private readonly ObservableCollection<Track> queue = new();
    private readonly ObservableCollection<Track> filtered = new();
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
    private string? preloadedPath;
    private bool preloadReady;
    private DateTime preloadStartedAt = DateTime.MinValue;
    private readonly System.Threading.SemaphoreSlim waveformWorker = new(1,1);
    private int analysisGeneration = 0;
    private double silenceThresholdDb = -42;
    private readonly Dictionary<string,double> manualMixOut = new(StringComparer.OrdinalIgnoreCase);
    private double AudioEnd(string path,double full) {
        if(manualMixOut.TryGetValue(path,out var manual))return Math.Clamp(manual,1,full);
        return audibleEnds.TryGetValue(path,out var end)?Math.Clamp(end,1,full):full;
    }
    private readonly Dictionary<string,double> audibleEnds = new(StringComparer.OrdinalIgnoreCase);
    private double EffectiveEndSeconds() {
        if(activeIndex<0||activeIndex>=queue.Count||!player.NaturalDuration.HasTimeSpan)return 0;
        var full=player.NaturalDuration.TimeSpan.TotalSeconds;
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
    private readonly HashSet<string> libraryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random random = new();
    private double playbackVolume = 0.85;
    private readonly string[] audioExtensions = { ".mp3", ".wav", ".wma", ".aac", ".m4a", ".flac" };
    private readonly string stateFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KYMA_SOHO", "library.json");
    public MainWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = queue; LibraryList.ItemsSource = filtered; LibraryListA.ItemsSource = filtered; LibraryListB.ItemsSource = filtered; HistoryList.ItemsSource = history;
        for (int i=0;i<9;i++) { var n=i; var b=new Button { Content=$"CART {i+1}", Background=new SolidColorBrush(Color.FromRgb((byte)(65+i*10),(byte)(37+i*6),(byte)(61+i*4))), Tag=i, FontSize=14, FontWeight=FontWeights.Bold, Foreground=Brushes.White, BorderBrush=new SolidColorBrush(Color.FromRgb(85,112,143)), BorderThickness=new Thickness(1), Margin=new Thickness(3), ToolTip="Αριστερό κλικ: PLAY · Δεξί κλικ: φόρτωση Cart" }; b.Click += (_,_)=> { if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))StopSingleCart(n);else PlayCart(n); }; b.MouseRightButtonUp += (_,_)=>AssignCart(n); CartGrid.Children.Add(b); cartPlayers[n].MediaEnded += (_,_)=>Dispatcher.BeginInvoke(()=>UpdateCartPad(n,false)); }
        searchDebounce.Tick += (_,_) => { searchDebounce.Stop(); RefreshLibrary(); };
        saveDebounce.Tick += (_,_)=>{saveDebounce.Stop();if(fading){pendingStateSave=true;return;}FlushState();};
        ConfigureAudioMenus();
        Title="KYMA RADIO AUTOMATION | SOHO Professional 2.5 BETA";
        Loaded += (_,_)=>UpdateVersionLabels(this);
        player.MediaEnded += Player_MediaEnded;
        incomingPlayer.MediaEnded += Player_MediaEnded;
        player.MediaFailed += (_,e)=>Dispatcher.BeginInvoke(() => { StatusText.Text="PLAYBACK ERROR: " + e.ErrorException.Message; stopped=true; });
        player.MediaOpened += Player_MediaOpened; incomingPlayer.MediaOpened += Player_MediaOpened;
        incomingPlayer.MediaFailed += (_,e)=>Dispatcher.BeginInvoke(()=>{
            preloadReady=false;preloadedPath=null;
            if(fading){FinishCrossfade();StatusText.Text="NEXT AUDIO ERROR";}
            else StatusText.Text="PRELOAD ERROR: "+e.ErrorException.Message;
        });
        timer.Tick += (_,_)=> {
            if(fading) {
                var clockNow=DateTime.UtcNow;
                if(!paused)fadeElapsedSeconds+=Math.Clamp((clockNow-lastFadeClock).TotalSeconds,0,0.5);
                lastFadeClock=clockNow;
                ApplyFadeVolumes();
                if(fadeElapsedSeconds>=outgoingFadeDuration)FinishCrossfade();
            } else if(auto&&!stopped&&!paused&&crossfadeSeconds>0&&player.NaturalDuration.HasTimeSpan) {
                double remaining=EffectiveEndSeconds()-player.Position.TotalSeconds;
                if(remaining>0&&remaining<=crossfadeSeconds)BeginCrossfade();
            }
            if(pendingStateSave&&!fading){pendingStateSave=false;SaveState();}
            UpdateFadeCountdown();UpdateWavePlayhead();Clock.Text=DateTime.Now.ToString("HH:mm:ss"); if(player.NaturalDuration.HasTimeSpan && !stopped) NowTime.Text=$"{player.Position.ToString(@"mm\:ss")} / {player.NaturalDuration.TimeSpan.ToString(@"mm\:ss")}"; else NowTime.Text="00:00"; if(!stopped && player.NaturalDuration.HasTimeSpan) PlaybackProgress.Value=Math.Min(PlaybackProgress.Maximum,player.Position.TotalSeconds); };
        player.Volume=playbackVolume; incomingPlayer.Volume=playbackVolume; foreach(var cp in cartPlayers)cp.Volume=playbackVolume; timer.Start(); LoadState(); UpdateNext(); DrawWave(NowWaveform, null); DrawWave(NextWaveform, null);
        Closing += (_,_)=> { saveDebounce.Stop();FlushState(); player.Close(); incomingPlayer.Close(); foreach(var cp in cartPlayers)cp.Close(); };
    }
    private static void UpdateVersionLabels(DependencyObject root) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is TextBlock label && label.Text.Contains("BROADCAST CONSOLE v2.2.1 BETA"))
                label.Text=label.Text.Replace("v2.2.1 BETA","v2.5 BETA");
            UpdateVersionLabels(child);
        }
    }
    private void RefreshLibrary() {
        var term=SearchBox.Text.Trim();
        var matches=string.IsNullOrWhiteSpace(term)?library.ToList():library.Where(t=>t.Display.Contains(term,StringComparison.CurrentCultureIgnoreCase)||t.Path.Contains(term,StringComparison.CurrentCultureIgnoreCase)).ToList();
        filtered.Clear(); foreach(var t in matches)filtered.Add(t);
        
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
        importing=true; StatusText.Text="SCANNING FOLDER...";
        try {
            string root=dialog.FolderName;
            var found=await Task.Run(()=> {
                var paths=new List<string>(); var pending=new Stack<string>();pending.Push(root);
                while(pending.Count>0){
                    var folder=pending.Pop();
                    try {
                        foreach(var p in Directory.EnumerateFiles(folder))
                            if(audioExtensions.Contains(System.IO.Path.GetExtension(p),StringComparer.OrdinalIgnoreCase))paths.Add(p);
                        foreach(var sub in Directory.EnumerateDirectories(folder))pending.Push(sub);
                    } catch(UnauthorizedAccessException){} catch(IOException){}
                }
                return paths;
            });
            int added=0;
            foreach(var p in found)if(libraryPaths.Add(p)){library.Add(new Track{Path=p});added++;}
            RefreshLibrary();UpdateNext();SaveState();StatusText.Text=$"FOLDER IMPORT: {added} νέα τραγούδια";
        } catch(Exception ex){StatusText.Text="IMPORT ERROR: "+ex.Message;}
        finally{importing=false;}
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
    private void Player_MediaOpened(object? sender,EventArgs e) {
        Dispatcher.BeginInvoke(() => {
            if(ReferenceEquals(sender,player)&&player.NaturalDuration.HasTimeSpan)
                PlaybackProgress.Maximum=Math.Max(1,player.NaturalDuration.TimeSpan.TotalSeconds);
            if(ReferenceEquals(sender,incomingPlayer)&&preloadedPath!=null)preloadReady=true;
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
        while(next<queue.Count&&!File.Exists(queue[next].Path))next++;
        if(next>=queue.Count&&repeatPlaylist){next=0;while(next<queue.Count&&!File.Exists(queue[next].Path))next++;}
        return next<queue.Count&&next!=activeIndex?next:-1;
    }
    private void PreloadUpcoming() {
        if(fading)return;
        int next=FindUpcomingIndex();
        string? path=next>=0?queue[next].Path:null;
        if(path==preloadedPath)return;
        preloadedPath=null;preloadReady=false;
        try {
            incomingPlayer.Stop();incomingPlayer.Close();
            if(path==null)return;
            incomingPlayer.Volume=0;
            incomingPlayer.Open(new Uri(path));
            preloadedPath=path;preloadStartedAt=DateTime.UtcNow;
        } catch(Exception ex){preloadedPath=null;StatusText.Text="PRELOAD ERROR: "+ex.Message;}
    }
    private void BeginCrossfade() {
        if(fading||stopped||!auto)return;
        int next=FindUpcomingIndex();
        if(next<0)return;
        if(preloadedPath!=queue[next].Path){PreloadUpcoming();return;}
        if(!preloadReady){
            if((DateTime.UtcNow-preloadStartedAt).TotalSeconds>3)StatusText.Text="WAITING FOR NEXT AUDIO";
            return;
        }
        try {
            incomingPlayer.Volume=0;incomingPlayer.Play();outgoingFadeEnd=EffectiveEndSeconds();
            fadeStartPosition=player.Position.TotalSeconds;
            outgoingFadeDuration=Math.Max(0.1,outgoingFadeEnd-fadeStartPosition);
            fadeElapsedSeconds=0;lastFadeClock=DateTime.UtcNow;
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
    private void RemoveMissing_Click(object s,RoutedEventArgs e) {
        int removed=0;
        for(int i=library.Count-1;i>=0;i--)if(!File.Exists(library[i].Path)){libraryPaths.Remove(library[i].Path);library.RemoveAt(i);removed++;}
        for(int i=queue.Count-1;i>=0;i--)if(!File.Exists(queue[i].Path)){
            if(i==activeIndex){StopPlayback();activeIndex=-1;}
            else if(i<activeIndex)activeIndex--;
            queue.RemoveAt(i);
        }
        RefreshLibrary();UpdateNext();SaveState();StatusText.Text=$"MISSING FILES REMOVED: {removed}";
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
    private void StartAt(int index) { if(fading){incomingPlayer.Stop();incomingPlayer.Close();fading=false;fadeElapsedSeconds=0;player.Volume=playbackVolume;}preloadedPath=null;preloadReady=false; if(index<0||index>=queue.Count)return; var t=queue[index]; if(!File.Exists(t.Path)) { StatusText.Text="MISSING FILE: "+t.Path; return; } player.Stop(); player.Open(new Uri(t.Path)); player.Play(); activeIndex=index; stopped=false; paused=false; NowTitle.Text=t.Display; NowSub.Text=t.Path; QueueList.SelectedIndex=index; history.Insert(0,$"{DateTime.Now:HH:mm:ss}   {t.Display}"); if(history.Count>100)history.RemoveAt(history.Count-1); SaveState(); StatusText.Text="PLAYING"; PlaybackProgress.Value=0; UpdateNext(); DrawWave(NowWaveform,t.Path); }
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
                analysisGeneration++;audibleEnds.Clear();waveCache.Clear();
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
        if(!player.NaturalDuration.HasTimeSpan){FadeCountdown.Text="ΥΠΟΛΟΓΙΣΜΟΣ ΧΡΟΝΟΥ...";return;}
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
    private void SaveState() { if(loadingState)return;saveDebounce.Stop();saveDebounce.Start(); }
    private void FlushState() { try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stateFile)!); File.WriteAllText(stateFile,JsonSerializer.Serialize(new SavedState { Library=library.Select(t=>t.Path).ToList(),Queue=queue.Select(t=>t.Path).ToList(),Carts=carts,Auto=auto,Repeat=repeatPlaylist,Crossfade=crossfadeSeconds,PreventDuplicates=preventQueueDuplicates,SilenceThresholdDb=silenceThresholdDb,MixOut=new Dictionary<string,double>(manualMixOut),Volume=playbackVolume*100,History=history.ToList() })); } catch(Exception ex) { StatusText.Text="SAVE ERROR: "+ex.Message; } }
    private void LoadState() { loadingState=true;try { if(!File.Exists(stateFile))return; var state=JsonSerializer.Deserialize<SavedState>(File.ReadAllText(stateFile)); if(state==null)return; silenceThresholdDb=Math.Clamp(state.SilenceThresholdDb,-54,-30); manualMixOut.Clear();foreach(var entry in state.MixOut??new Dictionary<string,double>())if(entry.Value>0)manualMixOut[entry.Key]=entry.Value; auto=state.Auto; RefreshAutoButton(); repeatPlaylist=state.Repeat; RefreshRepeatButton(); crossfadeSeconds=Math.Clamp(state.Crossfade,0,15);CrossfadeSlider.Value=crossfadeSeconds;preventQueueDuplicates=state.PreventDuplicates;PreventDuplicatesCheck.IsChecked=preventQueueDuplicates; playbackVolume=Math.Clamp(state.Volume/100.0,0,1); player.Volume=playbackVolume; foreach(var cp in cartPlayers)cp.Volume=playbackVolume; VolumeSlider.Value=playbackVolume*100; foreach(var entry in state.History??new List<string>()) { if(history.Count>=100)break; history.Add(entry); } foreach(var p in state.Library.Where(File.Exists))if(libraryPaths.Add(p))library.Add(new Track{Path=p}); foreach(var p in state.Queue.Where(File.Exists))queue.Add(new Track{Path=p}); for(int i=0;i<Math.Min(9,(state.Carts?.Length ?? 0));i++) { if(state.Carts![i] is string p && File.Exists(p)) { carts[i]=p; UpdateCartPad(i,false); } } RefreshLibrary(); UpdateNext(); } catch(Exception ex) { StatusText.Text="LOAD ERROR: "+ex.Message; } finally {loadingState=false;} }
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
                return (values,audibleEnd);
            });
            if(generation!=analysisGeneration)return;
            audibleEnds[path]=result.audibleEnd;
            waveCache[path]=result.values;if(waveCache.Count>64){var oldest=waveCache.Keys.First();waveCache.Remove(oldest);audibleEnds.Remove(oldest);}
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
