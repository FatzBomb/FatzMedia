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
  # Two embedded text tracks (ENG-045: the chosen track must stay chosen).
  if [ ! -s "$media/subtitles2.mkv" ]; then
    s1="$(mktemp --suffix=.srt)"; s2="$(mktemp --suffix=.srt)"
    printf '%s\n' 1 '00:00:00,500 --> 00:00:09,000' 'English line' '' > "$s1"
    printf '%s\n' 1 '00:00:00,500 --> 00:00:09,000' 'German line' '' > "$s2"
    "$enc" -v error -y -i "$media/h264_1080p.mp4" -i "$s1" -i "$s2" -map 0 -map 1 -map 2 -c copy -c:s srt \
      -metadata:s:s:0 language=eng -metadata:s:s:1 language=ger -disposition:s:0 default "$media/subtitles2.mkv"
    rm -f "$s1" "$s2"
  fi
fi

# Format matrix and player modes (LinuxFormatMatrixNativeTests, LinuxPlayerModesNativeTests): the
# file set of scripts/smoke-backlog.ps1 test 1, plus a picture, a silent video and a music video.
if [ -n "$enc" ]; then
  f="$media/formats"; mkdir -p "$f"
  tone=(-f lavfi -i "sine=frequency=440:duration=12:sample_rate=48000" -ac 2)
  pic=(-f lavfi -i "testsrc2=size=640x360:rate=30:duration=12")
  gen "$f/tone.mp3" "${tone[@]}" -c:a libmp3lame -b:a 192k
  gen "$f/tone.mp2" "${tone[@]}" -c:a mp2
  gen "$f/tone.ac3" "${tone[@]}" -c:a ac3
  gen "$f/tone-aac.m4a" "${tone[@]}" -c:a aac
  gen "$f/tone-alac.m4a" "${tone[@]}" -c:a alac
  gen "$f/tone-alac.caf" "${tone[@]}" -c:a alac
  gen "$f/tone.m4r" "${tone[@]}" -c:a aac -f ipod
  gen "$f/tone.flac" "${tone[@]}" -c:a flac
  gen "$f/tone.wav" "${tone[@]}" -c:a pcm_s16le
  gen "$f/tone.aiff" "${tone[@]}" -c:a pcm_s16be
  gen "$f/tone.ogg" "${tone[@]}" -c:a libvorbis
  gen "$f/tone.opus" "${tone[@]}" -c:a libopus
  gen "$f/tone.wma" "${tone[@]}" -c:a wmav2
  gen "$f/tone.wv" "${tone[@]}" -c:a wavpack
  gen "$f/tone.tta" "${tone[@]}" -c:a tta
  gen "$f/tone.mka" "${tone[@]}" -c:a flac
  gen "$f/tone.au" "${tone[@]}" -c:a pcm_s16be
  gen "$f/tone.dts" "${tone[@]}" -c:a dca -strict -2
  gen "$f/audio-only.mp4" "${tone[@]}" -c:a aac
  gen "$f/audio-only.ts" "${tone[@]}" -c:a aac -f mpegts
  gen "$f/cover.png" -f lavfi -i "testsrc2=size=300x300" -frames:v 1
  gen "$f/cover-only.mp4" "${tone[@]}" -i "$f/cover.png" -map 0:a -map 1:v -c:a aac -c:v png -disposition:v attached_pic
  gen "$f/divx-mp3.avi" "${pic[@]}" "${tone[@]}" -c:v mpeg4 -vtag XVID -c:a libmp3lame
  gen "$f/mpeg2-mp2.mpg" "${pic[@]}" "${tone[@]}" -c:v mpeg2video -c:a mp2 -f mpeg
  gen "$f/video-aac.mp4" "${pic[@]}" "${tone[@]}" -c:v mpeg4 -c:a aac
  gen "$f/silent-video.mp4" "${pic[@]}" -c:v mpeg4
  gen "$f/picture.jpg" -f lavfi -i "testsrc2=size=640x360" -frames:v 1
  gen "$f/long-10min.wv" -f lavfi -i "sine=frequency=330:duration=600:sample_rate=48000" -ac 2 -c:a wavpack
fi
if [ -n "$enc" ]; then
  gen "$media/tone.mp3" -f lavfi -i "sine=frequency=440:duration=12:sample_rate=44100" -ac 2 -c:a libmp3lame -b:a 192k
  gen "$media/tone.flac" -f lavfi -i "sine=frequency=440:duration=12:sample_rate=44100" -ac 2 -c:a flac
fi

echo "export FATZBOMB_LINUX_FFMPEG_DIR=$ff/lib"
echo "export LD_LIBRARY_PATH=$ff/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}   # optional; the engine preloads by path"
echo "# media: $media (HLS: cd $media/hls && python3 -m http.server 8000)"
