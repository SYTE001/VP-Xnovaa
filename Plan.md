# Xnovaa Video Player – Plan

**Target:** Desktop app Windows 10 (64-bit), fokus ringan & smooth untuk RAM 4–8 GB.  
**Gaya UI:** Mengikuti mockup (dark, three‑pane layout, minimalis).

---

## 0. Tujuan & Batasan

- Mendukung playback video lokal dengan UI modern.
- RAM idle ≤ ~100 MB, saat play 1080p ≤ ~250 MB.
- Responsif di laptop low‑end (CPU dual‑core + 4/8 GB RAM).
- Tidak pakai Electron / Chromium. Native desktop saja.

---

## 1. Tech Stack (Tetap, jangan diubah)

- Bahasa: **C# 12**
- Runtime: **.NET 8 (Windows x64)**
- Framework UI: **WPF** (Windows Presentation Foundation)
- Video engine: **LibVLC** via **LibVLCSharp.WPF**
- Arsitektur: **MVVM ringan** (tanpa over‑engineering)
- Storage data: **JSON file** (System.Text.Json)
- Target OS: **Windows 10 21H1+**, hanya 64-bit
- Packaging: `dotnet publish` + Inno Setup / MSIX (step di bawah)

---

## 2. Struktur Solusi

Single solution: `Xnovaa.sln`

1. **Project:** `Xnovaa.App` (WPF, exe)
   - Folders:
     - `/Views`
     - `/ViewModels`
     - `/Models`
     - `/Services`
     - `/Assets` (icons, logo)
     - `/Styles`
     - `/Utils`

Tidak perlu project terpisah, cukup namespace terstruktur.

---

## 3. Dependensi NuGet

Tambahkan ke `Xnovaa.App`:

- `LibVLCSharp.WPF`
- `CommunityToolkit.Mvvm` (MVVM helper)
- `System.Text.Json` (built-in; gunakan explicit jika perlu)
- `Microsoft.Extensions.Logging` (opsional ringan)

---

## 4. Fitur V1 (MVP)

### 4.1 Playback Utama

- Play/pause.
- Seek bar dengan current time & total duration.
- Skip ±10 detik.
- Next / Previous track (dari playlist kanan).
- Volume slider + mute/unmute.
- Toggle: Fullscreen.
- Opsi: aktifkan hardware acceleration (DXVA2).

### 4.2 Navigasi Kiri

- Search bar: filter video di library/playlist by nama file.
- Menu:
  - Home
  - Library
  - Playlist
  - Recently Added
  - Favorites
  - Lokal:
    - Import Files (file picker, multi‑select).
    - Folders (add folder, scan recursive).
- Indikator Local Storage (progress bar + text `"xxx GB free of yyy GB"`).

### 4.3 Panel Tengah (Home/Player)

- Area video besar dengan border hitam.
- Subtitle support (jika file `.srt` dengan nama sama tersedia; load otomatis).
- Overlay teks kecil (judul video) di sudut kiri atas (opsional).

### 4.4 Panel Kanan (Playlist)

- Tab:
  - `Playlist`
  - `Now Playing`
- Tombol `+` untuk menambah video ke playlist aktif (from library / file).
- Daftar item:
  - Thumbnail
  - Nama file
  - Durasi
  - Icon status play/pause di item yang sedang diputar.
  - Menu konteks (titik tiga):
    - Play now
    - Remove from playlist
    - Mark as Favorite / Unfavorite
    - Reveal in Explorer
- Info di header playlist: `"{N} videos • {totalDuration}"`.

### 4.5 Favorites & Recently Added

- `Favorites`: filter video dengan `IsFavorite = true`.
- `Recently Added`: sort berdasarkan `DateAdded desc` (limit default 50).

### 4.6 Settings Minimal (bisa di menu context / file Settings.json)

- Remember last playlist & last played video.
- Remember last window size/position & theme (dark default).
- Toggle: hardware acceleration (default ON).
- Default seek step (±10 detik).

### 4.7 Keyboard Shortcuts

- Space: Play/Pause.
- Left/Right: seek −/+ 5 detik.
- Ctrl+Left/Right: seek −/+ 10 detik.
- Up/Down: volume ±5%.
- F: toggle fullscreen.
- Ctrl+O: open file(s).
- Delete: remove item dari playlist.

---

## 5. Data Model

### 5.1 `VideoItem`

```csharp
class VideoItem
{
    public Guid Id { get; set; }
    public string FilePath { get; set; }          // full path
    public string FileName { get; set; }          // display name
    public TimeSpan Duration { get; set; }
    public string? ThumbnailPath { get; set; }    // local cache path, nullable
    public bool IsFavorite { get; set; }
    public DateTime DateAdded { get; set; }
    public long FileSizeBytes { get; set; }
}
```

### 5.2 `Playlist`

```csharp
class Playlist
{
    public Guid Id { get; set; }
    public string Name { get; set; }              // e.g. "Default"
    public List<Guid> VideoIds { get; set; }      // order of videos
}
```

### 5.3 `AppState`

```csharp
class AppState
{
    public Guid? LastPlaylistId { get; set; }
    public Guid? LastVideoId { get; set; }
    public double LastVolume { get; set; }        // 0.0–1.0
    public bool IsMuted { get; set; }
    public bool UseHardwareAcceleration { get; set; }
    public double SeekStepSeconds { get; set; }   // default 10
    public WindowStateInfo WindowState { get; set; }
}
```

**`WindowStateInfo`**: posisi dan ukuran window.

### 5.4 Lokasi File Data

- Folder: **`%AppData%\Xnovaa\`**
  - **`library.json`** (list **`VideoItem`**)
  - **`playlists.json`**
  - **`state.json`**
  - **`thumbnails\`** (cache images)

---

## 6. Services

### 6.1 `IVideoLibraryService`

Tanggung jawab:

- Load/save **`library.json`**.
- Tambah/update/remove **`VideoItem`**.
- Scan folder dan tambahkan ke library (hindari duplikat via **`FilePath`**).

### 6.2 `IPlaylistService`

- Manage daftar **`Playlist`**.
- Playlist default (**`"Now Playing"`**).

### 6.3 `IPlaybackService`

Wrap LibVLC:

- Init LibVLC instance (singleton).
- Play / Pause / Stop.
- Load media dari **`VideoItem.FilePath`**.
- Seek, set volume, mute, fullscreen state.
- Event: **`PositionChanged`**, **`MediaEnded`**, **`DurationChanged`**.

### 6.4 `IThumbnailService`

- Generate thumbnail menggunakan LibVLC snapshot atau Media Foundation.
- Simpan ke **`thumbnails\{videoId}.jpg`**.
- Hanya generate saat item akan terlihat (lazy).
- Batasi jumlah parallel tasks.

### 6.5 `IStorageInfoService`

- Hitung total & free space untuk drive dari path library utama (default C:).
- Return GB dan persentase untuk progress bar.

### 6.6 `IAppStateService`

- Load & save **`state.json`** (on app startup/shutdown).

---

## 7. Arsitektur UI (WPF)

### 7.1 `MainWindow`

Layout grid 3 kolom:

- Column 0: sidebar kiri, width ~260px.
- Column 1: area video (auto).
- Column 2: panel playlist kanan, width ~320px.

Row utama:

- Row 0: App header (title bar custom, kecil).
- Row 1: Content (player & nav).
- Row 2: Bottom controls bar.

#### 7.1.1 Sidebar (kiri)

- Logo + nama "Xnovaa" di top.
- SearchBox (TextBox + icon).
- Navigation (ItemsControl / ListBox) dengan binding ke enum **`NavigationSection`**.
- "Local" section separator.
- Storage info di bottom (Text + ProgressBar).

Aktifkan **`VirtualizingStackPanel.IsVirtualizing="True"`** untuk semua list.

#### 7.1.2 Area Player (tengah)

- **`VideoView`** dari LibVLCSharp embedded di border hitam, stretch **`Uniform`**.
- Di bawahnya, seek bar:
  - Slider (0–1 sebagai progress).
  - TextBlock untuk currentTime dan totalDuration.

#### 7.1.3 Panel Playlist (kanan)

- Top: Tab untuk **`Playlist`** / **`Now Playing`**, + tombol **`+`**.
- Middle: ListBox dengan template item:
  - Thumbnail (Image)
  - Teks nama & durasi
  - Icon play/pause kecil & menu tiga titik
- Bottom: volume slider + tombol layout kecil (tidak wajib fungsi lengkap v1, boleh placeholder).

Gunakan **`ScrollViewer`** + virtualization.

#### 7.1.4 Bottom Controls

Susunan tombol (kiri ke kanan):

- Repeat/Shuffle (bisa placeholder bool, default Off).
- **`<< 10s`**
- Volume icon (sinkron dengan slider kanan).
- Tombol Play/Pause besar (circle).
- Previous.
- Next.
- **`+10s`**
- Subtitle toggle (untuk masa depan, boleh no-op v1).
- Fullscreen toggle.

---

## 8. Alur Kerja Utama

1. **Start aplikasi**
   - Load **`library.json`**, **`playlists.json`**, **`state.json`** (jika ada).
   - Init LibVLC.
   - Restore window & volume state.
   - Jika **`LastVideoId`** ada dan file masih tersedia:
     - Set sebagai selected, tetapi **belum auto play** (configurable; v1: manual).

2. **Import Files**
   - OpenFileDialog multi-select.
   - Untuk setiap file:
     - Jika belum ada di library (cek **`FilePath`**):
       - Read durasi (via LibVLC / MediaInfo ringan).
       - Create **`VideoItem`**.
   - Simpan library.
   - Tambah ke playlist aktif jika user pilih (v1: otomatis ke "Now Playing").

3. **Add Folder**
   - FolderBrowserDialog.
   - Scan recursive (filter ekstensi umum: **`.mp4, .mkv, .avi, .mov, .wmv`**).
   - Tambah ke library seperti di atas.

4. **Play Video**
   - User click item di playlist / library.
   - **`PlaybackService.Play(VideoItem)`**:
     - Stop media lama, dispose.
     - Load media baru.
     - Set hardware accel sesuai setting.
   - Update **`AppState.LastVideoId`**.
   - Update UI: highlight item.

5. **Seek & Time Update**
   - Timer atau event LibVLC (`PositionChanged`) update Slider + Text waktu.
   - Drag Slider => call **`SetPosition`**.

6. **End of Video**
   - Jika ada item berikut di playlist, auto play next.
   - Jika tidak, stop di akhir.

7. **Close App**
   - Save **`library.json`** (jika kotor), **`playlists.json`**, **`state.json`**.
   - Release LibVLC instance.

---

## 9. Optimasi RAM & Performa

### 9.1 Pengaturan LibVLC

Saat inisialisasi LibVLC, gunakan opsi:

```csharp
var options = new[]
{
    "--file-caching=300",
    "--avcodec-hw=dxva2",    // hardware acceleration
    "--no-video-title-show",
    "--quiet"
};
```

- Satu instance LibVLC global, satu **`MediaPlayer`** saja.
- Setelah selesai play satu file, panggil **`media.Dispose()`**.

### 9.2 Thumbnail & Gambar

- Thumbnail maximal width 160 px, JPEG kualitas sedang.
- Generate on-demand: ketika item scroll ke tampilan, cek **`ThumbnailPath`**:
  - Jika belum ada: queue task generate di background (Task.Run, batas concurrency 1–2).
- Saat binding ke Image:
  - Gunakan **`BitmapImage`** dengan **`DecodePixelWidth`** 160.
  - Setelah load, panggil **`Freeze()`** untuk mengurangi memory overhead.

### 9.3 Virtualization & Koleksi

- Semua daftar (library, playlist, favorites, recently) menggunakan:
  - **`ItemsControl`**/**`ListBox`** dengan
    - **`VirtualizingStackPanel.IsVirtualizing="True"`**
    - **`VirtualizingStackPanel.VirtualizationMode="Recycling"`**
- Gunakan **`ObservableCollection<VideoItem>`** untuk list yang sedang aktif saja.
- Jangan simpan duplicate list besar di memori: untuk filter,
  - gunakan **`CollectionViewSource`** dengan filter, bukan copy list.

### 9.4 GC & Objek Berat

- Hindari event handler yang menyebabkan memory leak; gunakan weak events atau unsub saat ViewModel dispose.
- Jangan simpan **`ImageSource`** besar di model; simpan hanya path string.

### 9.5 Setting Build

- Build x64 only.
- Optimize: **`Release`** dengan **`Optimize code`** true.
- Use ReadyToRun (opsional) saat publish.

---

## 10. Styling / Theme

- Font: **`Segoe UI`**, ukuran 12 (default), 14 untuk judul.
- Theme: Dark:
  - Background utama: **`#101218`**.
  - Panel: **`#151821`**.
  - Accent (highlight, slider progress): **`#4C8DFF`** (atau mirip).
  - Text utama: **`#FFFFFF`**, secondary: **`#A0A4B8`**.
- Simpan di **`/Styles/Colors.xaml`** dan **`Styles/Controls.xaml`**.

---

## 11. Langkah Implementasi (Urutan Kerja Agent)

1. **Setup Proyek**
   - Buat solusi **`Xnovaa.sln`**.
   - Buat WPF App **`.NET 8`** **`Xnovaa.App`**.
   - Tambah NuGet packages (lihat bagian 3).
   - Setup folder structure.

2. **Implement Models & Services Dasar**
   - Buat **`VideoItem`**, **`Playlist`**, **`AppState`**, **`WindowStateInfo`**.
   - Implement **`VideoLibraryService`** (load/save JSON).
   - Implement **`PlaylistService`**.
   - Implement **`AppStateService`**.

3. **Integrasi LibVLC**
   - Tambah LibVLCSharp initialization di **`App.xaml.cs`**.
   - Buat **`PlaybackService`** dengan **`LibVLC`** + **`MediaPlayer`**.
   - Buat simple test window untuk play satu file (pastikan berfungsi) lalu hapus test.

4. **Bangun UI MainWindow Skeleton**
   - XAML: tiga kolom layout + bottom bar.
   - Tempatkan **`VideoView`** di panel tengah.
   - Buat sidebar nav dan playlist kanan dengan dummy data.

5. **Implement MVVM**
   - **`MainViewModel`**:
     - Koleksi **`VideoItem`** untuk library & playlist.
     - Command untuk Play/Pause, Next/Prev, Seek, Volume, Fullscreen.
   - Binding controls di XAML ke **`MainViewModel`**.

6. **Hook Services ke ViewModel**
   - Load library & playlists saat startup.
   - Wire event: klik item di playlist => **`PlaybackService.Play`**.
   - Update posisi slider dari event **`PositionChanged`**.

7. **Search, Favorites, Recently Added**
   - Implement **`CollectionViewSource`** + filter untuk search text.
   - Toggle Favorite via context menu.
   - View untuk **`Recently Added`** dan **`Favorites`** cukup filter view, tidak buat list baru.

8. **ThumbnailService**
   - Implement generasi & caching thumbnail.
   - Tambah binding ke UI playlist.
   - Pastikan generasi berjalan di background tanpa freeze UI.

9. **Storage Info & Settings**
   - **`StorageInfoService`** untuk free/total.
   - Bind hasil ke progress bar & text.
   - Implement load/save **`AppState`**.

10. **Keyboard Shortcuts & Fullscreen**
    - Override **`OnKeyDown`** di MainWindow atau gunakan InputBindings.
    - Fullscreen: toggle **`WindowStyle=None`**, **`WindowState=Maximized`**, sembunyikan border.

11. **Optimasi & Profiling**
    - Jalankan di **`Release`**.
    - Uji:
      - Idle (tanpa video, library ±500 item).
      - Play 1080p mp4 10 menit.
    - Pantau RAM (Task Manager) dan perbaiki leak jika ada.

12. **Publish**
    - Command:

      ```bash
      dotnet publish -c Release -r win10-x64 --self-contained false -p:PublishSingleFile=true
      ```

    - Buat installer (Inno Setup) sederhana:
      - Target folder: **`%ProgramFiles%\Xnovaa\`**.
      - Shortcut di Desktop + Start Menu.

---

## 12. Kriteria Selesai V1

- App berjalan di Windows 10 64-bit tanpa error.
- Bisa import file & folder, play video, next/prev, seek, fullscreen.
- Playlist kanan berfungsi dengan highlight item aktif.
- Search bekerja pada library/playlist.
- Favorites & Recently Added berfungsi (berbasis library).
- Thumbnail tampil dengan scrolling halus (tanpa lag berat).
- RAM:
  - Idle ≤ 120 MB.
  - Play 1080p ≤ 250 MB (approx, tergantung file; gunakan ini sebagai patokan).
- Semua data (library, playlists, state) persistent antar sesi.
