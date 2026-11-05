#!/usr/bin/env bash
# Idempotent Linux sandbox setup for real playback: FFmpeg 9.0 LGPL shared libraries (matching
# FFmpeg.AutoGen 9.0.1.1, avcodec 63) plus small test media. Nothing is written into the repo.
# Override the root with FATZBOMB_NATIVE_ROOT; the FFmpeg build with FFMPEG_ASSET.
set -euo pipefail

root="${FATZBOMB_NATIVE_ROOT:-/opt/fatzbomb-native}"
asset="${FFMPEG_ASSET:-ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0}"
url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$asset.tar.xz"
ff="$root/ffmpeg"
media="$root/media"
mkdir -p "$ff" "$media"

if [ ! -e "$ff/lib/libavcodec.so.63" ]; then
  tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
  curl -fsSL -o "$tmp/ff.tar.xz" "$url"
  tar -xf "$tmp/ff.tar.xz" -C "$tmp"
  cp -a "$tmp/$asset"/lib "$tmp/$asset"/bin "$tmp/$asset"/LICENSE.txt "$ff"/
fi

# The LGPL CLI has no libx264/libx265; encode test media with any ffmpeg that has them.
enc=""
for c in "$ff/bin/ffmpeg" "$(command -v ffmpeg || true)"; do
  [ -x "$c" ] && "$c" -hide_banner -encoders 2>/dev/null | grep libx265 >/dev/null && { enc="$c"; break; }
done
[ -n "$enc" ] || echo "no ffmpeg with libx264/libx265 found (apt install ffmpeg); skipping media" >&2

gen() { # gen <output> <ffmpeg args...>
  local out="$1"; shift
  [ -s "$out" ] || "$enc" -v error -y "$@" "$out"
}
if [ -n "$enc" ]; then
  src=(-f lavfi -i "testsrc2=size=1920x1080:rate=30:duration=10" -f lavfi -i "anullsrc=r=48000:cl=stereo")
  gen "$media/h264_1080p.mp4" "${src[@]}" -t 10 -c:v libx264 -preset veryfast -crf 26 -g 60 -pix_fmt yuv420p -c:a aac -b:a 64k -shortest
  hdr=(-f lavfi -i "testsrc2=size=1920x1080:rate=30:duration=10" -t 10 -c:v libx265 -preset veryfast -crf 28 -pix_fmt yuv420p10le -tag:v hvc1
       -color_primaries bt2020 -colorspace bt2020nc)
  gen "$media/hevc_hdr10.mp4" "${hdr[@]}" -color_trc smpte2084 \
    -x265-params "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400:hdr10=1:repeat-headers=1:log-level=error"
  gen "$media/hevc_hlg.mp4" "${hdr[@]}" -color_trc arib-std-b67 \
    -x265-params "colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc:repeat-headers=1:log-level=error"
  if [ ! -s "$media/hls/index.m3u8" ]; then
    mkdir -p "$media/hls"
    "$enc" -v error -y -i "$media/h264_1080p.mp4" -c copy -f hls -hls_time 2 -hls_playlist_type vod \
      -hls_segment_filename "$media/hls/seg%03d.ts" "$media/hls/index.m3u8"
  fi
  # 30 s master playlist with three variants (480p/720p/1080p, each a TS with its own audio).
  if [ ! -s "$media/hls_multi/master.m3u8" ]; then
    mkdir -p "$media/hls_multi"
    (cd "$media/hls_multi" && "$enc" -v error -y -stream_loop 2 -i "$media/h264_1080p.mp4" \
      -filter_complex "[0:v]split=3[a][b][c];[a]scale=-2:480[v0];[b]scale=-2:720[v1];[c]null[v2]" \
      -map "[v0]" -map 0:a -map "[v1]" -map 0:a -map "[v2]" -map 0:a \
      -c:v libx264 -preset veryfast -g 60 -keyint_min 60 -sc_threshold 0 -b:v:0 800k -b:v:1 2000k -b:v:2 4000k \
      -c:a aac -b:a 64k -f hls -hls_time 2 -hls_playlist_type vod -master_pl_name master.m3u8 \
      -var_stream_map "v:0,a:0 v:1,a:1 v:2,a:2" -hls_segment_filename "v%v/seg%03d.ts" "v%v/index.m3u8")
  fi
  # Network audio and streaming checks (ENG-034): a 20 s MP3 and Ogg Vorbis tone (the Ogg file
  # carries title/artist tags) and a copy of the MP4 with its index (moov) first.
  mkdir -p "$media/audio"
  gen "$media/audio/tone.mp3" -f lavfi -i "sine=frequency=440:duration=20" -c:a libmp3lame -b:a 128k
  gen "$media/audio/tone.ogg" -f lavfi -i "sine=frequency=440:duration=20" -c:a libvorbis -metadata title="Ogg Title" -metadata artist="Ogg Artist"
  gen "$media/faststart.mp4" -i "$media/h264_1080p.mp4" -c copy -movflags +faststart
  # Embedded subtitle scan (ENG-039 P5-1): the clip with a four-cue SRT track, in Matroska.
  if [ ! -s "$media/subtitles.mkv" ]; then
    srt="$(mktemp --suffix=.srt)"
    printf '%s\n' 1 '00:00:01,000 --> 00:00:02,500' 'First line' '' 2 '00:00:03,000 --> 00:00:04,500' 'Second line' '' \
      3 '00:00:05,000 --> 00:00:06,500' 'Third line' '' 4 '00:00:07,000 --> 00:00:08,500' 'Fourth line' > "$srt"
    "$enc" -v error -y -i "$media/h264_1080p.mp4" -i "$srt" -map 0 -map 1 -c copy -c:s srt "$media/subtitles.mkv"
    rm -f "$srt"
  fi
fi

# BASS for Linux (026B, LinuxBassNativeTests): libbass, libbass_fx and libbassflac from un4seen.com,
# when the host is reachable (BASS is free for non-commercial use; nothing goes into the repo).
bass="$root/bass"
if [ ! -e "$bass/libbass.so" ]; then
  mkdir -p "$bass"
  tmpb="$(mktemp -d)"
  ok=1
  for z in bass24-linux.zip z/0/bass_fx24-linux.zip bassflac24-linux.zip; do
    curl -fsSL -o "$tmpb/$(basename "$z")" "https://www.un4seen.com/files/$z" || ok=0
  done
  if [ "$ok" = 1 ]; then
    for f in "$tmpb"/*.zip; do unzip -q -o "$f" -d "${f%.zip}"; done
    find "$tmpb" -path '*x86_64*' -name 'lib*.so' -exec cp {} "$bass/" \;
  else
    echo "BASS download failed (is www.un4seen.com allowed?); the BASS tests will skip" >&2
  fi
  rm -rf "$tmpb"
fi
if [ -n "$enc" ]; then
  gen "$media/tone.mp3" -f lavfi -i "sine=frequency=440:duration=12:sample_rate=44100" -ac 2 -c:a libmp3lame -b:a 192k
  gen "$media/tone.flac" -f lavfi -i "sine=frequency=440:duration=12:sample_rate=44100" -ac 2 -c:a flac
fi

echo "export FATZBOMB_LINUX_FFMPEG_DIR=$ff/lib"
[ -e "$bass/libbass.so" ] && echo "export FATZBOMB_LINUX_BASS_DIR=$bass   # needs a sound server: see LinuxBassNativeTests"
echo "export LD_LIBRARY_PATH=$ff/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}   # optional; the engine preloads by path"
echo "# media: $media (HLS: cd $media/hls && python3 -m http.server 8000)"
