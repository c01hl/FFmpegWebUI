# FFmpeg 7.0 → 9.0: Breaking Changes, Deprecations & Removals
### Impact analysis for the FFmpegWebUI Blazor command-line generator

**Audit date:** 2026-09-28
**Empirical host:** macOS, `ffmpeg version 9.0.2` / `ffprobe version 9.0.2` at `/opt/homebrew/bin/ffmpeg`
(`libavutil 61.1.102`, `libavcodec 63.1.102`, `libavformat 63.1.102`, `libavfilter 12.1.102`)

**Method:** Every claim below is either (a) verified against FFmpeg release-branch source fetched from the
canonical Git mirror, (b) verified against an upstream commit, or (c) verified by running the local 9.0.2
binary. Anything I could not prove is explicitly flagged **UNCERTAIN**. Items I tested empirically are
marked **[VERIFIED EMPIRICALLY]** with the exact command.

---

## 0. Executive summary — the 6 things that actually break this app

| # | Issue | Severity | Affects |
|---|---|---|---|
| 1 | **`-vsync` was removed in 9.0** (not 8.0 — it survived all of 8.0 and 8.1) | 🔴 Fatal | Any saved/emitted command using `-vsync 0/1/2` |
| 2 | **`-filter_complex_script` was removed in 9.0** | 🔴 Fatal | Any saved/emitted command using it |
| 3 | **`-spatial_aq` / `-temporal_aq` (underscore) removed from NVENC in 9.0** — the hyphenated `-spatial-aq` / `-temporal-aq` survive | 🟠 High | NVENC presets |
| 4 | **NVENC preset aliases removed in 9.0**: `default hp hq bd ll llhq llhp losslesshp`; `-cbr`; `-2pass`; `-rc vbr_hq cbr_hq vbr_minqp vbr_2pass cbr_ld_hq ll_2pass_quality ll_2pass_size` | 🟠 High | NVENC presets |
| 5 | **`av1_videotoolbox` does not exist as an encoder in any FFmpeg version** — the probe will always fail on macOS | 🟠 High | Hardware-encoder detection |
| 6 | **`-rtc` is not an FFmpeg option.** The VideoToolbox option is `-realtime`. An emitted `-rtc` fails with `Unrecognized option 'rtc'` | 🟠 High | VideoToolbox presets |

**Good news:** `-c:v`, `-c:a`, `-vcodec`, `-acodec`, `-map_metadata`, `-qcomp`, `-fps_mode`, `-frames:v`,
`-crf`, `-b:v`, `-preset`, `-tune`, `-async` and `nullsrc`/`lavfi` **all still work in 9.0.2**. The
`ffprobe -show_entries` JSON contract this app depends on is **unchanged** — `format_name` and
`r_frame_rate` are both still present and correctly named.

---

## 1. Version landscape (question 5)

### Latest release and branch

| Item | Value | Source |
|---|---|---|
| **Latest release** | **FFmpeg 9.0.2 "Lei"**, released **2026-09-18** | [ffmpeg.org/download.html](https://ffmpeg.org/download.html) |
| Latest release branch | **`release/9.0`**, cut from `master` **2026-06-26** | [ffmpeg.org/download.html](https://ffmpeg.org/download.html) |
| Previous branch still maintained | **8.1.3 "Hoare"**, released 2026-09-21 (branch cut 2026-03-08) | [ffmpeg.org/download.html](https://ffmpeg.org/download.html) |
| Older maintained branches | 8.0.3, 7.1.5, 6.1.6, 5.1.10, 4.4.8 | [ffmpeg.org/download.html](https://ffmpeg.org/download.html) |
| Local host | `ffmpeg version 9.0.2` — matches upstream latest | **[VERIFIED EMPIRICALLY]** `ffmpeg -version` |

> **Note:** there is an **8.1 branch between 8.0 and 9.0**. This matters enormously: several removals
> people attribute to "FFmpeg 8" actually happened in 9.0, and several things that were already gone
> in 7.0 are still gone.

### Packaged versions

| Channel | Version | Evidence |
|---|---|---|
| **Homebrew (macOS)** | **9.0.2** | `brew info ffmpeg` → `stable 9.0.2 (bottled), HEAD`; [formula](https://github.com/Homebrew/homebrew-core/blob/HEAD/Formula/f/ffmpeg.rb) points at `ffmpeg-9.0.2.tar.xz` **[VERIFIED EMPIRICALLY]** |
| **winget** – `Gyan.FFmpeg` | **9.0.2** | [winget-pkgs manifests/g/Gyan/FFmpeg](https://github.com/microsoft/winget-pkgs/tree/master/manifests/g/Gyan/FFmpeg) (version dirs: … 8.1.2, 9.0, 9.0.1, 9.0.2) |
| **winget** – `Gyan.FFmpeg.Shared` | **9.0.2** | same manifest tree, `Shared/` |
| **Chocolatey** | **9.0.2** | [community.chocolatey.org/packages/ffmpeg](https://community.chocolatey.org/packages/ffmpeg) (page title "FFmpeg 9.0.2") |
| **Scoop** – `main/ffmpeg` | **9.0.2** | [bucket/ffmpeg.json](https://raw.githubusercontent.com/ScoopInstaller/Main/master/bucket/ffmpeg.json) → `"version": "9.0.2"` |
| **Arch Linux** – `extra` | **9.0.2-1** (updated 2026-09-22) | [archlinux.org/packages/extra/x86_64/ffmpeg](https://archlinux.org/packages/extra/x86_64/ffmpeg/) |
| **Debian** – `stable` (trixie/13) | **7:7.1.5-0+deb13u1** | [tracker.debian.org/pkg/ffmpeg](https://tracker.debian.org/pkg/ffmpeg) |
| **Debian** – `testing` (forky) | **7:8.1.2-2** | idem |
| **Debian** – `unstable` (sid) | **7:9.0.2-1** | idem |
| **Debian** – `oldstable` (bookworm/12) | 7:5.1.9-0+deb12u1 | idem |
| **Ubuntu** (Launchpad) | newest published: **8.1.2-2ubuntu2**; also 8.0.1-3ubuntu2, 6.1.1-3ubuntu5, 4.4.2-0ubuntu0.22.04.1 | [launchpad.net/ubuntu/+source/ffmpeg](https://launchpad.net/ubuntu/+source/ffmpeg) — *suite mapping not pinned, see Uncertainties* |
| **Alpine** – `edge/community` | **8.1.2-r1** | [pkgs.alpinelinux.org](https://pkgs.alpinelinux.org/package/edge/community/x86_64/ffmpeg) |
| **Fedora** | **not in Fedora's own repos** — `packages.fedoraproject.org/pkgs/ffmpeg/ffmpeg/` returns *Not Found*; FFmpeg ships via **RPM Fusion** | [rpmfusion.org/Howto/Multimedia](https://rpmfusion.org/Howto/Multimedia). openSUSE security advisories reference openSUSE ffmpeg **8.1.2** |
| **evermeet.cx** (macOS static) | **9.0.2** | `https://evermeet.cx/ffmpeg/info/ffmpeg/release` → `"version":"9.0.2"` |
| **gyan.dev** (Windows) | **9.0.2** for the release build (nightlies are rolling `git-*`) | `https://www.gyan.dev/ffmpeg/builds/release-version` → `9.0.2` |
| **BtbN/FFmpeg-Builds** (Windows) | rolling auto-builds; assets published as `ffmpeg-master-latest-*`, `ffmpeg-n9.0-latest-*`, `ffmpeg-n8.1-latest-*` | [releases](https://github.com/BtbN/FFmpeg-Builds/releases) — latest auto-build dated **2026-09-27** |
| **johnvansickle.com** (Linux static) | ⚠️ **stale: `release: 7.0.2`, git master built 20240629** | [johnvansickle.com/ffmpeg](https://johnvansickle.com/ffmpeg/) |

**Practical consequence for this app:** a user on a fully-updated macOS / Arch / Homebrew / winget / Choco
box has **9.0.2**. A user on **Debian stable** has **7.1.5** — where `-vsync` *still works*. A user on
**Debian testing** has **8.1.2** — where `-vsync` *still works*. Any preset that must run on both must be
version-aware, because the same command is valid on 8.1 and invalid on 9.0.

---

## 2. `-formats`, `-codecs`, `-encoders` output format (question 2)

### Stream: **STDOUT, not stderr** — [VERIFIED EMPIRICALLY]

```
$ for c in -version -formats -codecs -encoders -decoders -muxers -hwaccels; do
    printf "%-12s stdout=%s stderr=%s\n" "$c" \
      "$(ffmpeg -hide_banner $c 2>/dev/null | wc -c)" \
      "$(ffmpeg -hide_banner $c 2>&1 1>/dev/null | wc -c)"
  done
-version     stdout=821      stderr=0
-formats     stdout=16991    stderr=0
-codecs      stdout=30585    stderr=0
-encoders    stdout=10982    stderr=0
-decoders    stdout=28708    stderr=0
-muxers      stdout=7920     stderr=0
-hwaccels    stdout=45       stderr=0
```

**All listing subcommands write to stdout with an empty stderr.** If your C# capture code reads stderr
for these, it will get nothing. (`-version` also goes to **stdout** — 821 bytes.)

### Exact `ffmpeg -hide_banner -encoders` output format (9.0.2) — [VERIFIED EMPIRICALLY]

```
$ ffmpeg -hide_banner -encoders 2>/dev/null | sed -n '1,13p'
Encoders:
 V..... = Video
 A..... = Audio
 S..... = Subtitle
 .F.... = Frame-level multithreading
 ..S... = Slice-level multithreading
 ...X.. = Codec is experimental
 ....B. = Supports draw_horiz_band
 .....D = Supports direct rendering method 1
 ------
 V....D a64multi             Multicolor charset for Commodore 64 (codec a64_multi)
 V....D a64multi5            Multicolor charset for Commodore 64, extended with 5th color (colram) (codec a64_multi5)
 V....D alias_pix            Alias/Wavefront PIX image
```

Row grammar:

```
<space> = one space
[1]     = V | A | S   (media type — note: NOT 'D'/'E' flags like -codecs)
[2]     = F | .       (frame-level multithreading)
[3]     = S | .       (slice-level multithreading)
[4]     = X | .       (experimental)
[5]     = B | .       (draw_horiz_band)
[6]     = D | .       (direct rendering method 1)
<space>
%-20s   = encoder name, left-padded to 20 chars
[space] = the long description (may be empty)
[optional] " (codec <avcodec-descriptor-name>)"  ← only when encoder name != codec name
```

The flag field is **always exactly 6 characters** in 9.0.2 — verified by collecting the distinct
flag-field values:

```
$ ffmpeg -hide_banner -encoders | awk 'NR>9 && NF>=2 {print substr($0,2,6)}' | sort -u
A.....  A....D  A..X.D  S.....  V.....  V....D  V..X.D  V.S..D  VF...D  VFS...  VFS..D
```

### ⚠️ The `(codec X)` suffix is **not** a 9.0 change — but it *will* break naive regexes

I initially suspected this suffix was new. **It is not.** It exists in every branch from 5.1 onward:

```
$ for br in release/4.4 release/5.1 release/6.1 release/7.0 release/7.1 release/8.0 release/8.1 master; do
    printf "%-14s hits=%s\n" "$br" "$(grep -c '(codec ' opt_common.c)"
  done
release/4.4    hits=0
release/5.1    hits=6      ← introduced here
release/6.1    hits=6
release/7.0    hits=6
release/7.1    hits=6
release/8.0    hits=6
release/8.1    hits=6
master         hits=6
```

In `fftools/opt_common.c` (all of 5.1…master), `print_codecs()` does:

```c
printf(" %c%c%c%c%c%c",
       get_media_type_char(desc->type),
       (codec->capabilities & AV_CODEC_CAP_FRAME_THREADS)   ? 'F' : '.',
       (codec->capabilities & AV_CODEC_CAP_SLICE_THREADS)   ? 'S' : '.',
       (codec->capabilities & AV_CODEC_CAP_EXPERIMENTAL)    ? 'X' : '.',
       (codec->capabilities & AV_CODEC_CAP_DRAW_HORIZ_BAND) ? 'B' : '.',
       (codec->capabilities & AV_CODEC_CAP_DR1)             ? 'D' : '.');

printf(" %-20s %s", codec->name, codec->long_name ? codec->long_name : "");
if (strcmp(codec->name, desc->name))
    printf(" (codec %s)", desc->name);
```

**Action:** if your regex is anchored to end-of-line after the description, it breaks on 45 of the 9.0.2
encoder lines (`libx264`, `hevc_videotoolbox`, `aac`, …). Use a tolerant pattern, e.g.
`^\s([VAS])(.{5})\s+(\S+)\s{2,}(.*?)(?:\s+\(codec\s+(\S+)\))?$`.

### `-codecs` format (unchanged)

10-char flag field with a **different** legend (adds `E` encode, `I` intra-only, `L` lossless):

```
$ ffmpeg -hide_banner -codecs 2>/dev/null | sed -n '1,12p'
Codecs:
 D..... = Decoding supported
 .E.... = Encoding supported
 ..V... = Video codec
 ..A... = Audio codec
 ..S... = Subtitle codec
 ..D... = Data codec
 ..T... = Attachment codec
 ...I.. = Intra frame-only codec
 ....L. = Lossy compression
 .....S = Lossless compression
 -------
 DEVI.S alias_pix            Alias/Wavefront PIX image
```

### `-formats` format (unchanged)

3-char flag field (`D` demux, `.E.` mux, `..d` device):

```
$ ffmpeg -hide_banner -formats 2>/dev/null | tail -n +5 | grep mp4
 D   mov,mp4,m4a,3gp,3g2,mj2 QuickTime / MOV
  E  mp4             MPEG-4 Part 14
```

> Note `mov,mp4,m4a,3gp,3g2,mj2` is a **single comma-joined token** for the demuxer — a whitespace-split
> parse gets it right, but a `\S+` capture with a comma assumption will not.

### Verdict on question 2

**No format change in 7.0/7.1/8.0/8.1/9.0.** The only real risk is (a) reading stderr instead of stdout,
and (b) an end-anchored regex that doesn't tolerate the long-standing `(codec X)` suffix.

---

## 3. ffprobe JSON output (question 3)

### The app's exact command — [VERIFIED EMPIRICALLY]

```
$ ffprobe -v error -select_streams v:0 \
    -show_entries format=duration,format_name:stream=codec_name,width,height,r_frame_rate \
    -of json "probe_test.mp4"
{
    "programs": [ ],
    "stream_groups": [ ],
    "streams": [
        { "codec_name": "h264", "width": 320, "height": 240, "r_frame_rate": "25/1" }
    ],
    "format": {
        "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
        "duration": "1.000000"
    }
}
```

* Exit code **0**; output goes to **stdout** (`stdout bytes=330 stderr bytes=0`).
* **`format_name` — still present, unchanged name and semantics.** ✅
* **`r_frame_rate` — still present**, emitted as a **string fraction** (`"25/1"`), not a float. ✅
* `duration` — string with 6 decimal places (`"1.000000"`). ✅
* `streams` is an **array**; `format` is an **object**. ✅

### Two behaviour notes that matter for a strict parser

1. **`programs` and `stream_groups` are now routinely emitted as empty arrays** whenever the `stream`
   section is requested. Characterised empirically:

   | `-show_entries` | top-level keys returned |
   |---|---|
   | `format=duration` | `['format']` |
   | `stream=codec_name` | `['programs','stream_groups','streams']` |
   | `format=duration,format_name:stream=codec_name` | `['programs','stream_groups','streams','format']` |
   | `format=duration:stream=codec_name:program=program_id` | `['programs','stream_groups','streams','format']` |

   For any real JSON parser (e.g. `System.Text.Json`) this is harmless. It only breaks a
   hand-rolled string scanner that assumes `"format"` is the first key.
   `stream_groups` is a 7.0-era addition (`ffprobe -show_stream_groups`, Changelog "version 7.0").
   *Whether `programs` was always emitted alongside `streams` in older releases is **UNCERTAIN** — I
   verified only 9.0.2.*

2. **`ffprobe -of json <file>` with no `-show_*` option prints `{}`.** Verified:
   ```
   $ ffprobe -of json probe_test.mp4 2>/dev/null
   {
   
   }
   ```
   The source confirms every `do_show_*` defaults to `0`:
   ```c
   static int do_show_format  = 0;
   static int do_show_streams = 0;
   static int do_show_programs = 0;
   static int do_show_stream_groups = 0;
   ```
   This is by design, not a 9.0 regression — but it means the app must **always** pass `-show_entries`
   (or `-show_format`/`-show_streams`). *I did not diff this behaviour against ≤7.x; treat the
   "unchanged across versions" claim as **UNCERTAIN**, though nothing in the 7.0→9.0 Changelogs touches it.*

### Additive (non-breaking) new stream fields in 9.0.2

`mime_codec_string` now appears in the stream section (e.g. `"avc1.64000d"`),
emitted by `ffprobe.c`:
```c
if (!av_mime_codec_str(par, stream->avg_frame_rate, &pbuf))
    print_str("mime_codec_string", pbuf.str);
```
The `disposition` object has also grown (`non_diegetic`, `captions`, `multilayer`, …) and
8.1 added `ffprobe: only show refs field in stream section when reading frames`.
All additive — they do not affect the app's `-show_entries` projection.
*Exact introduction release for `mime_codec_string`: **UNCERTAIN** (not pinned).*

### New ffprobe options (additive)

* **`ffprobe -codec`** (Changelog "version 8.1") **requires a media specifier**:
  ```
  $ ffprobe -v error -codec h264 -of json -show_streams probe_test.mp4
  No media specifier was specified for 'h264' in option 'codec'.
  Use -codec:<media_spec> where <media_spec> can be one of: 'a' (audio), 'v' (video), 's' (subtitle), 'd' (data)
  Failed to set value 'h264' for option 'codec': Invalid argument
  ```
  Correct form: `-codec:v h264`. (Note: 8.0's Changelog also lists `ffprobe -codec option`, but the
  specifier requirement is clearly enforced in 9.0.)
* `ffprobe -show_stream_groups` (7.0), `ffprobe -o` (5.1), `-output_format` (6.1) — all unaffected.

### Verdict on question 3

**The app's ffprobe command is safe on 9.0.2.** `format_name` and `r_frame_rate` are intact and
correct. No field renames or removals were found in 7.0→9.0 for the fields this app reads.

---

## 4. Hardware encoders 7.0 → 9.0 (question 4)

### Definitive encoder-registration diff

Derived from `libavcodec/allcodecs.c` on each release branch (`ff_*_encoder` symbols, filtered to
hardware families):

| Transition | Hardware encoders **ADDED** | Hardware encoders **REMOVED** |
|---|---|---|
| 7.0 → 7.1 | `h264_vulkan`, `hevc_vulkan`, `hevc_d3d12va` | — |
| 7.1 → 8.0 | `av1_vulkan`, `ffv1_vulkan`, `av1_mf`, `h264_oh`, `hevc_oh` | — |
| 8.0 → 8.1 | `av1_d3d12va`, `h264_d3d12va`, `h264_rkmpp`, `hevc_rkmpp`, `prores_ks_vulkan` | — |
| **8.1 → 9.0** | **none** | **`h264_omx`, `mpeg4_omx`** (OpenMAX — deprecated in 8.0 per Changelog) |

Totals: 7.0 = 257 encoders / 41 hardware-ish → **9.0 = 270 encoders / 52 hardware-ish**.

**9.0 adds no new hardware encoders.** The AV1 Vulkan *encoder* listed in the Changelog appears only
under `version <next>` (i.e. post-9.0 master), not under `version 9.0`.

### Names that changed / did not change

| Question | Answer | Evidence |
|---|---|---|
| Is `h264_vaapi` still present? | **Yes** | `ff_h264_vaapi_encoder` in release/9.0 `allcodecs.c`; `libavcodec/vaapi_encode_h264.c` present |
| `hevc_vaapi`, `av1_vaapi`, `vp9_vaapi`, `mjpeg_vaapi`, `mpeg2_vaapi`, `vp8_vaapi` | **All still present, names unchanged** | release/9.0 `allcodecs.c` |
| `h264_nvenc`, `hevc_nvenc`, `av1_nvenc` | **Present, names unchanged** | idem |
| `h264_qsv`, `hevc_qsv`, `av1_qsv`, `mjpeg_qsv`, `mpeg2_qsv`, `vp9_qsv` | **Present, names unchanged.** No oneVPL rename of encoder names was found | idem; `libavcodec/qsvenc_{h264,hevc,av1,jpeg,mpeg2,vp9}.c` |
| `h264_amf`, `hevc_amf`, `av1_amf` | **Present, names unchanged** | idem |
| Vulkan encoders | `h264_vulkan`, `hevc_vulkan`, `av1_vulkan`, `ffv1_vulkan`, `prores_ks_vulkan` | idem |
| **`av1_videotoolbox`** | **DOES NOT EXIST AS AN ENCODER — in any version** | see below |
| `h264_videotoolbox`, `hevc_videotoolbox`, `prores_videotoolbox` | **The only 3 VideoToolbox encoders** | release/9.0 `allcodecs.c` registers exactly these 3 |

### `av1_videotoolbox` — hard evidence it is decoder-only

```
$ grep -in videotoolbox libavcodec/allcodecs.c     # release/9.0
875:extern const FFCodec ff_h264_videotoolbox_encoder;
890:extern const FFCodec ff_hevc_videotoolbox_encoder;
907:extern const FFCodec ff_prores_videotoolbox_encoder;
```

Only **encoder** symbols. The repo *does* contain `libavcodec/videotoolbox_av1.c`, but that is the
**decoder** (`av1_videotoolbox` as a `-hwaccel`/decoder), alongside `videotoolbox_vp9.c`.
No file or symbol named `av1_videotoolbox_encoder` exists anywhere in the release/9.0 tree.

**[VERIFIED EMPIRICALLY]** on 9.0.2:
```
$ ffmpeg -hide_banner -f lavfi -i nullsrc=s=256x256:d=0.1 -c:v av1_videotoolbox \
    -frames:v 1 -f null - -y
[vost#0:0 @ …] Unknown encoder 'av1_videotoolbox'
[vost#0:0 @ …] Error selecting an encoder
exit code = 8
```

> **Action:** remove `av1_videotoolbox` from the hardware-encoder candidate list, or keep it only as a
> probe that is *expected* to fail. Note the probe failure signature: **exit code 8**, empty stdout,
> and `Unknown encoder '<name>'` on stderr.

### NVENC — the biggest 9.0 break (Changelog: *"Remove deprecated NVENC options and support for pre-11.1 SDK versions"*)

Seven commits, all committed **2026-06-23/24** — i.e. **before the 9.0 branch cut on 2026-06-26**, so all
land in **9.0** (and none are in 8.1):

| Commit | What it removed |
|---|---|
| [`927ffd09`](https://github.com/FFmpeg/FFmpeg/commit/927ffd0930fa3f7a948d4ed30c67bd518f9549ce) | `-vsync` (ffmpeg CLI, not NVENC) |
| [`a66a0223`](https://github.com/FFmpeg/FFmpeg/commit/a66a02233228723a5e15bc40072dafaf1c658f08) | **deprecated preset aliases** |
| [`2b054bc9`](https://github.com/FFmpeg/FFmpeg/commit/2b054bc9e660513ab2acfcdc97cb118874102e80) | **`-cbr`, `-2pass`** |
| [`dcf339c4`](https://github.com/FFmpeg/FFmpeg/commit/dcf339c413e8bd21b5ceae710be97bf2bf5f82ab) | **old/deprecated `-rc` modes** |
| [`64e6d750`](https://github.com/FFmpeg/FFmpeg/commit/64e6d750f9e72ee0edeedf1b0ed4fe42a8a2976b) | **`-spatial_aq`, `-temporal_aq`** (underscore aliases) |
| [`677c61b8`](https://github.com/FFmpeg/FFmpeg/commit/677c61b8fe0751df9f7557ae66443336538cef91) | `-global_quality` now **rejected** instead of aliasing `-qp` |
| [`1c2e6304`](https://github.com/FFmpeg/FFmpeg/commit/1c2e63045641d40a11400dd130763f24b6d690b0) | `FF_API_NVENC_H264_MAIN` — **h264_nvenc now defaults to High profile** |
| [`64951333`](https://github.com/FFmpeg/FFmpeg/commit/6495133363ef86856401310d4dcf854fed4d8e7c) | support for **NVENC SDK < 11.1** (raises the minimum NVIDIA driver) |

**Exhaustive AVOption name diff, extracted from `nvenc_{h264,hevc,av1}.c` on release/8.1 vs release/9.0:**

```
h264_nvenc  REMOVED in 9.0 (16):
  2pass  bd  cbr_hq  cbr_ld_hq  default  hp  ll_2pass_quality  ll_2pass_size
  llhp  llhq  losslesshp  spatial_aq  temporal_aq  vbr_2pass  vbr_hq  vbr_minqp

hevc_nvenc  REMOVED in 9.0 (16):
  2pass  bd  cbr_hq  cbr_ld_hq  default  hp  ll_2pass_quality  ll_2pass_size
  llhp  llhq  losslesshp  spatial_aq  temporal_aq  vbr_2pass  vbr_hq  vbr_minqp

av1_nvenc   REMOVED in 9.0 (1):
  default

NOTHING was ADDED to any of the three in 9.0
(except av1_nvenc gained `hierarchical` as a b_ref_mode value)
```

**What survives in 9.0 (answer to your specific questions):**

| Option | 9.0 status |
|---|---|
| **`-cq`** | ✅ **Still present** on `h264_nvenc`, `hevc_nvenc`, `av1_nvenc` |
| **`-rc`** | ✅ Present, but values are now **only `constqp`, `vbr`, `cbr`** (the old `vbr_hq`, `cbr_hq`, `vbr_minqp`, `vbr_2pass`, `cbr_ld_hq`, `ll_2pass_*` are gone). The removal commit's own warning said: *"Use -rc constqp/cbr/vbr, -tune and -multipass instead."* |
| **`-preset`** | ✅ Present. **`p1`…`p7` still work**, as do **`slow`, `medium`, `fast`**. The legacy names `default/hp/hq/bd/ll/llhq/llhp/losslesshp` are **removed**. (`hq`, `uhq`, `ll`, `ull`, `lossless` are *not* legacy aliases — they are the new-style quality/feature presets and **survive**.) |
| **`-tune`** | ✅ Present |
| **`-multipass`** | ✅ Present |
| **`-spatial-aq` / `-temporal-aq`** (hyphen) | ✅ **Present** |
| **`-spatial_aq` / `-temporal_aq`** (underscore) | ❌ **Removed in 9.0** |
| `-b:v` | ✅ Present | 
| `-rc-lookahead`, `-surfaces`, `-aq-strength`, `-zerolatency`, `-nonref_p`, `-strict_gop`, `-b_adapt`, `-b_ref_mode`, `-dpb_size`, `-lookahead_level`, `-profile`, `-level`, `-tier`, `-g`, `-bf`, `-refs`, `-qcomp`, `-qblur`, `-qdiff` | ✅ All present |
| `-qp`, `-init_qpP/B/I`, `-qp_cb_offset`, `-qp_cr_offset`, `-qmin`, `-qmax` | ✅ All present |
| `-cbr`, `-2pass` | ❌ Removed |
| `-global_quality` | ❌ Now **rejected** (previously a deprecated alias for `-qp`) |

### QSV / oneVPL

* Encoder **names unchanged** (`h264_qsv`, `hevc_qsv`, `av1_qsv`, `mjpeg_qsv`, `mpeg2_qsv`, `vp9_qsv`).
  No oneVPL-driven rename found.
* 7.0 Changelog: *"Change the default bitrate control method from VBR to CQP for QSV encoders."* —
  a **behavioural** change; if the app emits `-c:v h264_qsv -b:v 4M` expecting VBR, quality/bitrate
  semantics differ from 6.1.
* 7.1 Changelog: *"`qsv_params` option added for QSV encoders."*
* 6.0 Changelog already recorded *"oneVPL support for QSV"* — so oneVPL is not a 7.0→9.0 change.
* ⚠️ QSV/VAAPI/AMF/NVENC/D3D are **compile-time optional**; none appear in a standard macOS
  Homebrew build. See §5.

### VideoToolbox encoder options across releases

Option-name sets extracted from `libavcodec/videotoolboxenc.c`:

```
7.0 (35): allow_sw require_sw realtime frames_before frames_after prio_speed power_efficient
          max_ref_frames profile… level coder… a53cc constant_bit_rate max_slice_bytes qmin qmax
          main10 alpha_quality auto proxy lt standard hq 4444 xq
7.1 (36): + b
8.0 (39): + spatial_aq  + main42210  + rext
8.1 (39): (no change)
9.0 (39): (no change)   ← IDENTICAL to 8.1
```

Per-encoder subsets in 9.0:

```
h264_videotoolbox (16): profile baseline constrained_baseline main high constrained_high extended
                        level coder cavlc vlc cabac ac a53cc constant_bit_rate max_slice_bytes
                        [+ shared: allow_sw require_sw realtime frames_before frames_after
                         prio_speed power_efficient spatial_aq max_ref_frames]
hevc_videotoolbox (7):  profile main main10 main42210 rext alpha_quality constant_bit_rate
                        [+ shared, incl. allow_sw / realtime / spatial_aq / max_ref_frames]
prores_videotoolbox (8): profile auto proxy lt standard hq 4444 xq
```

Confirmed by `ffmpeg -h encoder=hevc_videotoolbox` on 9.0.2, which lists
`-alpha_quality`, `-constant_bit_rate`, `-allow_sw`, `-require_sw`, `-realtime`,
`-frames_before`, `-frames_after`, `-prio_speed`, `-power_efficient`, `-spatial_aq`,
`-max_ref_frames`, profiles `main/main10/main42210/rext`, and supported pixel formats
`videotoolbox_vld nv12 yuv420p bgra ayuv p010le p210le`.

**No VideoToolbox encoder option was added or removed between 7.0 and 9.0 except:**
`b` (7.1), `spatial_aq`/`main42210`/`rext` (8.0).

---

## 5. Compile-time availability (the trap for the hardware probe)

The app's probe runs `-c:v <encoder>` and treats success/failure as "encoder available". That is correct
in principle, but **on macOS Homebrew builds almost nothing is available**:

**[VERIFIED EMPIRICALLY]** — `ffmpeg -version` configuration on this host:
```
--enable-shared --enable-pthreads --enable-version3 --enable-ffplay --enable-gpl
--enable-libsvtav1 --enable-libopus --enable-libx264 --enable-libmp3lame --enable-libdav1d
--enable-libvmaf --enable-libvpx --enable-libx265 --enable-openssl
--enable-videotoolbox --enable-audiotoolbox --enable-neon
```
Note: **no `--enable-nvenc`, no `--enable-libvpl`/`--enable-qsv`, no `--enable-vaapi`,
no `--enable-amf`, no `--enable-vulkan`, no `--enable-d3d12va`, no `--enable-omx`.**

Probe results for the app's exact command on 9.0.2:

```
ffmpeg -f lavfi -i nullsrc=s=256x256:d=0.1 -c:v <enc> -frames:v 1 -f null - -y

  h264_videotoolbox      PRESENT+WORKS rc=0
  hevc_videotoolbox      PRESENT+WORKS rc=0
  prores_videotoolbox    PRESENT+WORKS rc=0
  av1_videotoolbox       ABSENT(unknown encoder)
  h264_vulkan            ABSENT(unknown encoder)
  hevc_vulkan            ABSENT(unknown encoder)
  av1_vulkan             ABSENT(unknown encoder)
  h264_nvenc             ABSENT(unknown encoder)
  hevc_nvenc             ABSENT(unknown encoder)
  av1_nvenc              ABSENT(unknown encoder)
  h264_qsv               ABSENT(unknown encoder)
  h264_amf               ABSENT(unknown encoder)
  h264_vaapi             ABSENT(unknown encoder)
  h264_d3d12va           ABSENT(unknown encoder)

$ ffmpeg -hide_banner -encoders | grep vulkan      →  (empty)
$ ffmpeg -hide_banner -hwaccels
Hardware acceleration methods:
videotoolbox
```

> **Implication:** `h264_vulkan` / `av1_vulkan` are real upstream encoders (7.1 / 8.0) but are absent
> from mainstream macOS builds. Also absent on **Fedora/Debian/Ubuntu default builds** for
> nvenc/qsv/amf (licensing/packaging). The probe must remain the source of truth; do **not** hardcode
> "Vulkan encoders exist since 8.0" as an availability assumption.

---

## 6. VideoToolbox and lavfi/`nullsrc` specifics (question 6)

### `-tag:v hvc1` — real and necessary

**[VERIFIED EMPIRICALLY]** — 9.0.2, default tag vs explicit:

```
$ ffmpeg -f lavfi -i nullsrc=s=64x64:d=0.2 -c:v hevc_videotoolbox /tmp/vt_def.mp4 -y
$ ffprobe -v error -show_entries stream=codec_name,codec_tag_string -of default=nw=1 /tmp/vt_def.mp4
codec_name=hevc
codec_tag_string=hev1          ← DEFAULT IS hev1

$ ffmpeg -f lavfi -i nullsrc=s=64x64:d=0.2 -c:v hevc_videotoolbox -tag:v hvc1 /tmp/vt_hvc1.mp4 -y
$ ffprobe … /tmp/vt_hvc1.mp4
codec_name=hevc
codec_tag_string=hvc1          ← with -tag:v hvc1
```

So: FFmpeg does **not** warn and does **not** default to `hvc1`. `-tag:v hvc1` is genuinely required for
Apple/QuickTime/Safari-friendly output, and `-tag:v hvc1` remains fully supported in 9.0.2. The source
even defines `enum { kCMVideoCodecType_HEVC = 'hvc1' };` and sets `avctx->codec_tag` accordingly.
*(The "requirement" itself is an Apple/QuickTime platform constraint, not an FFmpeg-enforced one —
FFmpeg happily writes `hev1`.)*

### `-pix_fmt` requirement for VideoToolbox

**No new hard `-pix_fmt` requirement was introduced in 8.0/9.0.** `hevc_videotoolbox` advertises
`Supported pixel formats: videotoolbox_vld nv12 yuv420p bgra ayuv p010le p210le` and encodes
`yuv420p` input successfully with no explicit `-pix_fmt`
(**[VERIFIED EMPIRICALLY]**: the `nullsrc` probe returns rc=0).
`-pix_fmt p010le` is needed if you want 10-bit.

### `-allow_sw`

* Present since **at least 7.0** (in the 7.0 option table) and still present in 9.0.2. No change.
* **[VERIFIED EMPIRICALLY]**: passing `-allow_sw 1` with a non-VideoToolbox encoder produces only
  `Codec AVOption allow_sw (Allow software encoding) has not been used for any stream…` — i.e. it is
  accepted but inert, not an error.

### `-rtc` — **this option does not exist**

**[VERIFIED EMPIRICALLY]**
```
$ ffmpeg -hide_banner -f lavfi -i nullsrc=s=64x64:d=0.05 -rtc -f null - -y
Unrecognized option 'rtc'.
Error splitting the argument list: Option not found
```

The correct VideoToolbox option is **`-realtime`**:
```
-realtime          <boolean>    E..V....... Hint that encoding should happen in real-time if not
                                            faster (e.g. capturing from camera). (default false)
```
Confirmed present in 7.0, 7.1, 8.0, 8.1 and 9.0 option tables — **unchanged**.
There is no option named `rtc` anywhere in `videotoolboxenc.c` on any of those branches.
*(Possibly the app's `-rtc` was intended for `ddagrab`/other filters; mark as a latent bug either way.)*

### `nullsrc` / lavfi

**Unchanged and working.** **[VERIFIED EMPIRICALLY]**
```
$ ffmpeg -hide_banner -loglevel error -nostdin -f lavfi -i nullsrc=s=256x256:d=0.1 \
    -c:v h264_videotoolbox -frames:v 1 -f null - -y    → rc=0
```
`-f lavfi -i nullsrc=s=<W>x<H>:d=<sec>` parses fine; `-frames:v 1` still caps output at one frame
(verified: `-frames:v 1` on a 2-second `testsrc2` produces a single-frame encode).
`-f null -` as a sink is unchanged.

---

## 7. Full option-by-option status (question 1)

### Still working on 9.0.2 — [VERIFIED EMPIRICALLY]

Each tested with `ffmpeg -f lavfi -i nullsrc=s=64x64:d=0.05 <opt> -f null - -y`:

| Option | 9.0.2 result |
|---|---|
| `-c:v libx264`, `-c:a aac` | ✅ accepted |
| `-vcodec <enc>`, `-acodec aac` | ✅ accepted (legacy aliases retained) |
| `-map_metadata -1` | ✅ accepted |
| `-qcomp 0.6` | ✅ accepted (video codec AVOption) |
| `-fps_mode cfr` | ✅ accepted (documented at `ffmpeg.texi:2391`) |
| `-frames:v 1` | ✅ accepted, still caps frame count |
| `-crf 23`, `-preset ultrafast`, `-tune film`, `-b:v 0` with `libx264` | ✅ accepted |
| `-b:v 1M` | ✅ accepted |
| `-async 1` (with `-c:a aac`) | ✅ accepted and ran, rc=0 |
| `-allow_sw 1` | ✅ accepted (inert warning for non-VT encoders) |

### Removed — [VERIFIED EMPIRICALLY] on 9.0.2

| Option | 9.0.2 error | Removed in | Replacement |
|---|---|---|---|
| `-vsync` | `Unrecognized option 'vsync'.` / `Error splitting the argument list: Option not found` (rc=8) | **9.0** | `-fps_mode` |
| `-vsync drop` / `-fps_mode drop` | (removed) | **9.0** | `-fps_mode passthrough` |
| `-same_quant` | `Unrecognized option 'same_quant'.` | long before 7.0 | none |
| `-filter_complex_script` | `Unrecognized option 'filter_complex_script'.` | **9.0** | `-/filter_complex <file>` ✅ verified working |
| `-qphist` | `Unrecognized option 'qphist'.` | **9.0** | none (was a no-op) |
| `-rtc` | `Unrecognized option 'rtc'.` | never existed | `-realtime` (VideoToolbox) |
| `-psnr` | `Unrecognized option 'psnr'.` | **7.0** (per Changelog: *"removed deprecated ffmpeg CLI options -psnr and -map_channel"*) | `-vf psnr` filter |
| `-map_channel` | `Unrecognized option 'map_channel'.` | **7.0** | `-filter_complex` `pan`/`channelmap` |

> ⚠️ **`-top`**: the source-level removal commit exists
> ([`7a6c1b19`](https://github.com/FFmpeg/FFmpeg/commit/7a6c1b19ab12eda975f34295b4542611c3d6d0f7),
> *"fftools/ffmpeg: Remove deprecated -top option"*, and `"top"` has 0 registrations in release/9.0's
> `ffmpeg_opt.c`), **but my empirical test did not print an "Unrecognized option" line.** My test
> harness captured `stderr` with a `head -3` that showed only the input banner — I do **not** consider
> the `-top` result conclusive either way. Treat `-top` as **removed in 9.0 per source + commit**, but
> flag the empirical confirmation as **UNCERTAIN**.

### Removed via Changelog only (no local repro attempted)

| Item | Release | Changelog quote |
|---|---|---|
| `-filter_complex_script` | 9.0 | (commit, not changelog) |
| OpenMAX encoders (*deprecation*) | 8.0 | *"OpenMAX encoders deprecated"* |
| OpenMAX encoders (*removal*, `h264_omx`/`mpeg4_omx`) | 9.0 | derived from `allcodecs.c` diff |
| `vf_scale2ref` deprecated | 7.1 | *"vf_scale2ref deprecated"* |
| old HLS protocol handler | 8.1 | *"Remove the old HLS protocol handler"* |
| CELT decoding, ogg/celt parsing | 9.0 | *"Remove CELT decoding support (doesn't affect Opus CELT)"*, *"Remove ogg/celt parsing"* |
| yasm support | 8.0 | *"yasm support dropped, users need to use nasm"* (build-time only) |
| CrystalHD decoders deprecated | 6.0 | (before your window; still present as deprecated) |

### 7.1 stream-specifier syntax changes — **will break emitted `-map` strings**

Verbatim from the Changelog, `version 7.1`:

> * minor stream specifier syntax changes:
>     - when matching by metadata (`:m:<key>:<val>`), the colon character in keys or values now has to be backslash-escaped
>     - in optional maps (`-map ....?`) with a metadata-matching stream specifier, the value has to be separated from the question mark by a colon, i.e. `-map ....:m:<key>:<val>:?` (otherwise it would be ambiguous whether the question mark is a part of `<val>` or not)
>     - **multiple stream types in a single specifier (e.g. `:s:s:0`) now cause an error, as such a specifier makes no sense**

If the app emits `-map` strings with metadata matchers or compound type specifiers, these are
**9.0-relevant hard errors**. Also from 7.1: *"stream specifiers in fftools can now match by stream disposition"*.

### Other 7.0 CLI-relevant changes

* `version 7.0`: *"ffmpeg CLI `-bsf` option may now be used for input as well as output"* — additive.
* `version 7.0`: *"ffmpeg CLI options may now be used as `-/opt <path>`, which is equivalent to
  `-opt <contents of file <path>>`"* — this is the **replacement for `-filter_complex_script`**.
* `version 7.0`: *"demuxing, decoding, filtering, encoding, and muxing in the ffmpeg CLI now all run in
  parallel"* — **behavioural**: log interleaving and error ordering can change; parse **exit codes**,
  not stderr text.
* `version 7.0`: *"ffmpeg CLI loopback decoders"*.
* `version 6.1` (context): *"ffmpeg CLI `-top` option deprecated in favor of the setfield filter"* —
  this is the deprecation that 9.0's removal completes.

### `-crf` handling (explicitly asked)

`-crf` is **not** an ffmpeg CLI option — it is a *codec-private AVOption* provided by encoders like
`libx264`, `libx265`, `libsvtav1`, `libvpx-vp9`. Consequences verified on 9.0.2:

* It is accepted globally and forwarded; `ffmpeg -c:v libx264 -crf 23` works.
* When no encoder consumes it, FFmpeg emits a **warning, not an error**:
  ```
  [out#0/null @ …] Codec AVOption crf (Constant Rate Factor value) has not been used for any stream.
  The most likely reason is either wrong type (e.g. a video option with no video streams) or that it
  is a private option of some decoder which was not actually used for any stream.
  ```
* **This means `-crf` on hardware encoders (nvenc/videotoolbox/qsv) silently does nothing.** For NVENC
  the equivalent is `-cq`; for VideoToolbox it is `-b:v`/`-q:v`-style quality (VT has no `-crf`).
  The same "accepted but inert" warning pattern applies to `-preset` and `-tune` when the selected
  encoder has no such option:
  ```
  [out#0/null @ …] Codec AVOption preset (Encoding preset) has not been used for any stream. …
  [out#0/null @ …] Codec AVOption tune (Tune the encoding to a specific scenario) has not been used for any stream. …
  ```
  **⚠️ This is a silent-correctness bug in the app today**: `{crf}`, `{preset}`, `{tune}` placeholders
  that are meaningful for libx264/libx265 are silently ignored for `*_videotoolbox` / `*_nvenc`.
  Consider warning when a CRF/preset/tune placeholder is combined with a hardware encoder.

### `-filter_complex` behaviour

`-filter_complex` **still works** ([VERIFIED EMPIRICALLY], `-filter_complex "scale=32:32"` → rc=0) and is
documented in `ffmpeg.texi` (25 hits). Only the `_script` variant was removed. Note that 7.0's
parallel-pipeline rework and 7.1's *"ffmpeg CLI filtergraph chaining"* can change the **order** of
diagnostics on stderr — parse exit codes, not log text.

---

## 8. Breaking changes that matter for this app — before / after

### 8.1 🔴 `-vsync` → `-fps_mode` (removed in **9.0**, works in ≤8.1)

```diff
- ffmpeg -i {input} -c:v {encoder} -crf {crf} -vsync 0 {output}
+ ffmpeg -i {input} -c:v {encoder} -crf {crf} -fps_mode passthrough {output}
```

`-vsync` numeric → `-fps_mode` string mapping (from the 9.0 source `parse_and_set_vsync()`):

| `-vsync` (≤8.1) | `-fps_mode` (7.1+, required from 9.0) |
|---|---|
| `-vsync 0` / `-vsync passthrough` | `-fps_mode passthrough` |
| `-vsync 1` / `-vsync cfr` | `-fps_mode cfr` |
| `-vsync 2` / `-vsync vfr` | `-fps_mode vfr` |
| `-vsync -1` / `-vsync auto` | `-fps_mode auto` |
| `-vsync drop` | ❌ removed entirely (deprecated in 8.x, **gone in 9.0**) → `-fps_mode passthrough` |

Failure mode on 9.0: `Unrecognized option 'vsync'.` + `Error splitting the argument list: Option not found`, **exit code 8**.
On 8.0/8.1 this still works, so the app must gate on the detected major version.

### 8.2 🔴 `-filter_complex_script` → `-/filter_complex` (removed in **9.0**)

```diff
- ffmpeg -i {input} -filter_complex_script "graph.txt" {output}
+ ffmpeg -i {input} -/filter_complex "graph.txt" {output}
```
**[VERIFIED EMPIRICALLY]** — `-/filter_complex /tmp/fc2.txt` returns rc=0 on 9.0.2, while
`-filter_complex_script` returns `Unrecognized option 'filter_complex_script'.`

### 8.3 🟠 NVENC option names (9.0)

```diff
  # preset — numeric p1..p7 and slow/medium/fast still fine, aliases gone
- ffmpeg -i {input} -c:v h264_nvenc -preset hq -rc vbr_hq -b:v 8M {output}
+ ffmpeg -i {input} -c:v h264_nvenc -preset p5 -rc vbr -b:v 8M {output}

- ffmpeg -i {input} -c:v hevc_nvenc -preset llhq {output}
+ ffmpeg -i {input} -c:v hevc_nvenc -preset p1 -tune ll {output}

- ffmpeg -i {input} -c:v h264_nvenc -cbr 1 {output}
+ ffmpeg -i {input} -c:v h264_nvenc -rc cbr {output}

- ffmpeg -i {input} -c:v h264_nvenc -2pass 1 {output}
+ ffmpeg -i {input} -c:v h264_nvenc -multipass 2 {output}

- ffmpeg -i {input} -c:v h264_nvenc -spatial_aq 1 -temporal_aq 1 {output}
+ ffmpeg -i {input} -c:v h264_nvenc -spatial-aq 1 -temporal-aq 1 {output}

- ffmpeg -i {input} -c:v h264_nvenc -global_quality 23 {output}
+ ffmpeg -i {input} -c:v h264_nvenc -qp 23 {output}      # -global_quality now REJECTED

  # quality knob is unchanged — this is still correct on 9.0:
  ffmpeg -i {input} -c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq 23 -b:v 0 {output}
```

Also note **`h264_nvenc` now defaults to High profile** (was Main) in 9.0 — a silent behaviour change
for presets that relied on the old default.

### 8.4 🟠 `av1_videotoolbox` is not an encoder — remove it from the candidate list

```diff
  # app's hardware probe
- for enc in h264_videotoolbox hevc_videotoolbox av1_videotoolbox; do
+ for enc in h264_videotoolbox hevc_videotoolbox prores_videotoolbox; do
      ffmpeg -f lavfi -i nullsrc=s=256x256:d=0.1 -c:v "$enc" -frames:v 1 -f null - -y
  done
```
`av1_videotoolbox` exits **8** with `Unknown encoder 'av1_videotoolbox'` on every macOS build, in every
FFmpeg version. VideoToolbox has exactly **three** encoders: `h264_videotoolbox`, `hevc_videotoolbox`,
`prores_videotoolbox`.

### 8.5 🟠 `-rtc` → `-realtime` (VideoToolbox)

```diff
- ffmpeg -f avfoundation -i "0" -c:v h264_videotoolbox -rtc 1 {output}
+ ffmpeg -f avfoundation -i "0" -c:v h264_videotoolbox -realtime 1 {output}
```
`-rtc` produces `Unrecognized option 'rtc'.` + `Option not found`.

### 8.6 🟡 `hevc_videotoolbox` + `-tag:v hvc1` — keep it, it is still needed

```diff
  ffmpeg -i {input} -c:v hevc_videotoolbox -tag:v hvc1 -b:v 6M {output}
```
Default output tag is **`hev1`** (verified). `-tag:v hvc1` still works in 9.0.2 and remains necessary
for QuickTime/Safari/Apple-platform playability. No change needed — but do not drop it.

### 8.7 🟡 Hardware-probe detection must not assume Vulkan/QSV/NVENC presence

```diff
- // assume: Vulkan encoders exist since 8.0, so offer h264_vulkan on any 8.0+ build
+ // never assume — always probe. macOS Homebrew builds have --enable-videotoolbox only;
+ // no vulkan / nvenc / qsv / vaapi / amf / d3d12va / omx.
```
```diff
- ffmpeg -i {input} -c:v h264_vulkan {output}          # fails: Unknown encoder on most builds
+ ffmpeg -i {input} -c:v h264_videotoolbox {output}    # probe-confirmed available
```

### 8.8 🟡 7.1 stream-specifier syntax

```diff
- -map 0:s:s:0                       # multiple stream types in one specifier: now an ERROR
+ -map 0:s:0

- -map 0:m:language:eng?             # old optional-map form with metadata matcher
+ -map 0:m:language:eng:?            # value must be separated from '?' by a colon
```

### 8.9 🟢 No change required (but worth hardening)

```diff
  ffmpeg -version                     # ✅ stdout, 821 bytes
  ffmpeg -formats -hide_banner        # ✅ stdout, unchanged 3-char flags
  ffmpeg -codecs   -hide_banner       # ✅ stdout, unchanged 10-char flags
  ffmpeg -encoders -hide_banner       # ✅ stdout, 6-char flags, "(codec X)" suffix (present since 5.1)
```
```diff
  ffprobe -v error -select_streams v:0 \
    -show_entries format=duration,format_name:stream=codec_name,width,height,r_frame_rate \
    -of json "{file}"                 # ✅ unchanged; format_name + r_frame_rate intact, stdout, rc=0
```
```diff
  ffmpeg -f lavfi -i nullsrc=s=256x256:d=0.1 -c:v {encoder} -frames:v 1 -f null - -y
                                      # ✅ lavfi/nullsrc/-frames:v/-f null all unchanged
```

---

## 9. Recommended code changes (priority order)

1. **Version-gate `-fps_mode` vs `-vsync`.** Detect the major version from `ffmpeg -version`
   (`^ffmpeg version (\d+)`), and emit `-fps_mode` for ≥7.1 (safe, since `-fps_mode` exists from 7.0/7.1)
   or unconditionally if you drop support for ≤7.0. **Never emit `-vsync`** — it is removed in 9.0.
2. **Replace `-filter_complex_script` with `-/filter_complex <path>`** (9.0-safe, and the
   `-/opt` form works from 7.0).
3. **Fix NVENC presets**: replace `-spatial_aq`/`-temporal_aq` with hyphenated forms; drop `-cbr`,
   `-2pass`, and the removed `-preset`/`-rc` aliases.
4. **Remove `av1_videotoolbox`** from the candidate-encoder list (or keep it flagged as never-available).
5. **Fix `-rtc` → `-realtime`** for VideoToolbox.
6. **Warn on inert placeholders**: if `{crf}` / `{preset}` / `{tune}` is combined with a hardware encoder,
   surface a warning — FFmpeg accepts them and silently ignores them (verified warning text in §7).
7. **Harden the `-encoders` regex** to tolerate the `(codec <name>)` suffix and to read **stdout**.
8. **Keep `-tag:v hvc1`** for `hevc_videotoolbox`.
9. **Never assume compile-time hardware support** — the probe is authoritative. macOS Homebrew builds
   have `videotoolbox` only.
10. **Add a regression fixture** for `-map` strings with metadata matchers / compound type specifiers (7.1 rule changes).

---

## 10. Uncertainties (explicitly flagged)

| # | Item | Status |
|---|---|---|
| 1 | `-top` removal: source shows 0 registrations in release/9.0 and the removal commit exists, but my empirical run did not cleanly capture the `Unrecognized option` line | **Source: removed in 9.0. Empirical confirmation: UNCERTAIN** |
| 2 | Whether the empty `programs` array was emitted alongside `streams` before 9.0 (`stream_groups` is confirmed ≥7.0) | **UNCERTAIN** — verified only on 9.0.2 |
| 3 | Whether `ffprobe` with no `-show_*` options ever defaulted to printing format+streams. Source in 9.0 shows all `do_show_*` default to 0 | **UNCERTAIN for ≤7.x**; source-verified for 9.0 |
| 4 | Release that introduced the `mime_codec_string` stream field | **UNCERTAIN** (present in 9.0.2; additive, does not affect the app) |
| 5 | Ubuntu version→suite mapping (observed `8.1.2-2ubuntu2`, `8.0.1-3ubuntu2`, `6.1.1-3ubuntu5`, `4.4.2-0ubuntu0.22.04.1` on Launchpad; only the last is confidently 22.04) | **UNCERTAIN (partially)** |
| 6 | NixOS / nixpkgs FFmpeg version | **NOT DETERMINED** — search.nixos.org not fetched successfully |
| 7 | RPM Fusion FFmpeg version (Fedora's own repos do not ship FFmpeg) | **NOT DETERMINED** — rpmfusion pkgdb not fetchable; openSUSE advisories reference 8.1.2 |
| 8 | Docker image tags (`jrottenberg/ffmpeg`, `linuxserver/ffmpeg`) | **NOT INVESTIGATED** (out of scope / low value) |
| 9 | Whether any NVENC *default* values changed in 9.0 beyond the h264 profile change | **UNCERTAIN** — only the profile default was confirmed via commit message |
| 10 | `ffmpeg.org`'s `gitweb` (git.ffmpeg.org) is behind an Anubis bot-challenge and returns *Access Denied*; all source evidence above comes from the canonical GitHub mirror (`github.com/FFmpeg/FFmpeg`), which mirrors the same repository | **Methodological note** |

---

## 11. References

### Official FFmpeg

* Changelog (all releases): <https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/Changelog> · <https://github.com/FFmpeg/FFmpeg/blob/master/Changelog>
* Download / release list / branch dates: <https://ffmpeg.org/download.html>
* Release notes 9.0: <https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/9.0/RELEASE_NOTES>
* Release notes 8.0: <https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/8.0/RELEASE_NOTES>
* ffmpeg CLI docs (9.0): <https://ffmpeg.org/ffmpeg.html> · texi source: <https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/9.0/doc/ffmpeg.texi>
* ffprobe docs: <https://ffmpeg.org/ffprobe.html>
* `doc/APIchanges`: <https://raw.githubusercontent.com/FFmpeg/FFmpeg/release/9.0/doc/APIchanges>

### Removal / change commits

* `-vsync` removed (9.0): <https://github.com/FFmpeg/FFmpeg/commit/927ffd0930fa3f7a948d4ed30c67bd518f9549ce>
* `-vsync drop` / `-fps_mode drop` removed: <https://github.com/FFmpeg/FFmpeg/commit/687e061cb20765a2a67a04fa2914853a512a6fee>
* `-filter_complex_script` removed: <https://github.com/FFmpeg/FFmpeg/commit/07407fff6142f14dcb21b8a06d0d15db0e31135e>
* `-top` removed: <https://github.com/FFmpeg/FFmpeg/commit/7a6c1b19ab12eda975f34295b4542611c3d6d0f7>
* `-qphist` removed: <https://github.com/FFmpeg/FFmpeg/commit/d77b5234b509c8323bdf5e0c6f07cbf4beb252c1>
* NVENC option aliases (`spatial_aq`/`temporal_aq`): <https://github.com/FFmpeg/FFmpeg/commit/64e6d750f9e72ee0edeedf1b0ed4fe42a8a2976b>
* NVENC preset aliases: <https://github.com/FFmpeg/FFmpeg/commit/a66a02233228723a5e15bc40072dafaf1c658f08>
* NVENC `-cbr` / `-2pass`: <https://github.com/FFmpeg/FFmpeg/commit/2b054bc9e660513ab2acfcdc97cb118874102e80>
* NVENC deprecated `-rc` modes: <https://github.com/FFmpeg/FFmpeg/commit/dcf339c413e8bd21b5ceae710be97bf2bf5f82ab>
* NVENC `global_quality` rejected: <https://github.com/FFmpeg/FFmpeg/commit/677c61b8fe0751df9f7557ae66443336538cef91>
* h264_nvenc High-profile default: <https://github.com/FFmpeg/FFmpeg/commit/1c2e63045641d40a11400dd130763f24b6d690b0>
* NVENC SDK <11.1 dropped: <https://github.com/FFmpeg/FFmpeg/commit/6495133363ef86856401310d4dcf854fed4d8e7c>

### Source files inspected (release branches)

* `libavcodec/nvenc.c`, `nvenc.h`, `nvenc_h264.c`, `nvenc_hevc.c`, `nvenc_av1.c` — 8.1 vs 9.0
* `libavcodec/allcodecs.c` — 7.0 / 7.1 / 8.0 / 8.1 / 9.0
* `libavcodec/videotoolboxenc.c` — 7.0 / 7.1 / 8.0 / 8.1 / 9.0
* `fftools/ffmpeg_opt.c` — 7.1 / 8.0 / 8.1 / 9.0 / master
* `fftools/opt_common.c` — 4.4 / 5.1 / 6.1 / 7.0 / 7.1 / 8.0 / 8.1 / 9.0 / master
* `fftools/ffprobe.c` — 9.0
* `doc/encoders.texi` — all branches (note: **NVENC is not documented in `doc/encoders.texi`** on any branch; AVOptions are only visible via `ffmpeg -h encoder=<name>`)

### Package sources

* Homebrew: <https://github.com/Homebrew/homebrew-core/blob/HEAD/Formula/f/ffmpeg.rb>
* winget: <https://github.com/microsoft/winget-pkgs/tree/master/manifests/g/Gyan/FFmpeg>
* Chocolatey: <https://community.chocolatey.org/packages/ffmpeg>
* Scoop: <https://raw.githubusercontent.com/ScoopInstaller/Main/master/bucket/ffmpeg.json>
* Arch Linux: <https://archlinux.org/packages/extra/x86_64/ffmpeg/>
* Debian: <https://tracker.debian.org/pkg/ffmpeg> · <https://packages.debian.org/search?keywords=ffmpeg>
* Ubuntu: <https://launchpad.net/ubuntu/+source/ffmpeg>
* Alpine: <https://pkgs.alpinelinux.org/package/edge/community/x86_64/ffmpeg>
* RPM Fusion: <https://rpmfusion.org/Howto/Multimedia>
* evermeet.cx (macOS static): <https://evermeet.cx/ffmpeg/>
* gyan.dev (Windows): <https://www.gyan.dev/ffmpeg/builds/>
* BtbN (Windows/Linux): <https://github.com/BtbN/FFmpeg-Builds/releases>
* johnvansickle (Linux static): <https://johnvansickle.com/ffmpeg/>

### Third-party (non-primary, used only as leads)

* heise — FFmpeg 9.0 overview: <https://www.heise.de/en/news/FFmpeg-9-0-WebP-in-GIF-out-11398216.html>
* claude-video issue #143 (`-vsync` removal breakage report): <https://github.com/bradautomates/claude-video/issues/143>
  — ⚠️ this issue attributes the removal to "ffmpeg 8+"; **my source review shows `-vsync` was still
  registered in `release/8.0` and `release/8.1` and removed only in 9.0.** Treat the issue's version
  claim as inaccurate.

---

*Report compiled from primary sources only (FFmpeg release-branch source, upstream commits, official
docs) plus direct execution of `ffmpeg`/`ffprobe` 9.0.2 on the local host.*
