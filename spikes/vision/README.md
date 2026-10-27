# Visual search spike

Throwaway experiment for the roadmap item "search for objects and concepts with a small local AI model".
It measures how well two small models find things like "dog" or "chart" on screenshots, and how fast.
Nothing here ships in the app: this folder is not part of `SearchAnything.slnx`, and the app still has no
network code and no models.

## What's here

| Path | What it is |
|---|---|
| `fetch.ps1` | Downloads the models (Hugging Face, pinned revisions) and test photos (Wikimedia Commons) into git-ignored folders, checking every file's SHA-256 |
| `eval/scenes.html` | Builds 17 realistic 1920x1080 screens (photo gallery, news article, shop, dashboard, slides, chat, inbox, map, code editor, video, calendar, social feed, spreadsheet, desktop, scanned PDF, thumbnail wall, photos app with 3 px gutters) and records where every labelled thing is |
| `eval/photos.json` | The 80 photos used, with labels, source, author and license |
| `eval/render.ps1` | Renders the screens to `data/screens/` with headless Edge |
| `VisionSpike/` | C# runner: tokenizers, ONNX Runtime (CPU or DirectML GPU), region finder, scoring |

```powershell
.\spikes\vision\fetch.ps1
.\spikes\vision\eval\render.ps1
cd spikes\vision\VisionSpike
# Best setup so far: SigLIP 2, bare query as prompt, thumbnail walls split, up to 128 regions
dotnet run -c Release -- eval --model siglip --variant b16 --template "{q}" --text-fp32 --split-grids --max-regions 128 [--gpu --vision-file vision_model_fp16.onnx]
dotnet run -c Release -- eval --model siglip --variant b32 ...   # patch 32 at 256 px: about 2.5x faster, less accurate
dotnet run -c Release -- eval --model owl --tiles 768 [--gpu]
```

Each run writes a report to `results/<run>.md`. Add `--draw 0.01` to save each screen with its highlights drawn on it.

## How it's scored

Every one of 28 queries runs on every screen, so most screen/query pairs have nothing to find. Three queries
(giraffe, guitar, fire truck) never appear at all. A highlight counts as right if most of it sits on a
correct target (or it overlaps one well), and as neutral if it sits on something the photo arguably also shows
(a horse photo with mountains behind it). Everything else is a false alarm.

- **Recall:** targets highlighted, split by size (small means under 120 px on a side).
- **Precision:** highlights that are right.
- **Quiet when absent:** screens without the thing that show no highlight at all.
- **Presence AUC:** how well the best score on a screen tells whether the thing is there at all.

## Round 2: SigLIP 2 B/32, region cap and thumbnail splitting (17 screens)

The scene with 3 px gutters was added for this round, so these numbers aren't directly comparable with round 1. All runs use the bare query as the prompt and the fp32 text model.

| SigLIP 2 setup | Threshold | Recall (small / medium / large) | Precision | Quiet when absent | Top highlight right | Per screenshot, CPU | Per screenshot, GPU (fp16) |
|---|---|---|---|---|---|---|---|
| B/16, up to 64 regions (round 1 setup) | 0.005 | 58% (61 / 51 / 89) | 64% | 95% | 91% | | 0.43 s |
| B/16, up to 128 regions | 0.005 | 67% (82 / 51 / 89) | 66% | 95% | 91% | | 0.44 s |
| **B/16, up to 128 regions, walls split** | 0.005 | **84%** (82 / 86 / 89) | 70% | 95% | 94% | 2.8 s | **0.43 s** |
| same | 0.01 | 78% (71 / 82 / 89) | 76% | 97% | | | |
| B/32, up to 128 regions | 0.002 | 68% (85 / 52 / 89) | 52% | 91% | 91% | | 0.18 s |
| **B/32, up to 128 regions, walls split** | 0.002 | **86%** (85 / 85 / 89) | 56% | 91% | 94% | **1.0 s** | 0.19 s |
| same | 0.005 | 72% (69 / 71 / 89) | 66% | 95% | | | |

Per keystroke (text only), every setup costs 13–28 ms. Screens have 14–91 regions, median 38.

- **Round 1 misdiagnosed the small-object misses.** The cause was my cap of 64 regions, which cut off most of the 78-thumbnail wall plus the coarse grid. The 4 px texture blocks already separate thumbnails with 6 px gaps. Raising the cap to 128 lifted small-target recall from 61% to 82%.
- **Splitting walls along their gutters matters once gutters are tight.** On the 3 px photos-app screen, the region finder sees one blob; splitting gives one region per thumbnail. That lifted medium-target recall from 51% to 86% (B/16). The split only fires on a regular grid of at least three similar-sized pieces, so it never cut a photo along a band of sky.
- **B/32 at 256 px is about 2.5× cheaper per region** (CPU 27 ms vs 73 ms; GPU fp16 about 5 ms vs 11 ms) but less accurate: F1 0.69 vs 0.77, and its scores run lower, so it needs a lower threshold and shows more wrong highlights.

## Round 1 results (16 screens; Core Ultra 7 258V, Arc 140V iGPU, 2026-10-27)

| | SigLIP 2 B/16 + region finder | OWL-ViT B/32, 6 tiles | OWL-ViT B/32, whole screen |
|---|---|---|---|
| Recall (at best threshold) | 68–73% | 65% | 37% |
| … small / medium / large | 52–61% / 86–90% / 83–89% | 61% / 85% / 28% | 21% / 63% / 33% |
| Precision | 63–78% | 69% | 45% |
| Quiet when absent | 95–98% | 94% | 96% |
| Top highlight right, when present | 92–95% | 86% | 61% |
| Presence AUC | 0.98 | 0.93 | 0.88 |
| Concepts (chart, table, map, calendar, code, QR) | mostly found; chart types get confused | mostly missed | mostly missed |
| Image cost per screenshot, CPU | 1–5 s (about 75 ms per region, median 28–37 regions) | (runs per query, see next row) | (runs per query) |
| Image cost per screenshot, GPU (DirectML) | 0.5–0.8 s median with fp16 | | |
| Cost per keystroke | 13–30 ms (text only) | 1.6 s CPU, 0.77 s GPU (this export repeats the image every query) | 0.27 s CPU |
| Download | 186–372 MB vision + 1.1 GB text (fp32) | 612 MB | 612 MB |

Other findings:

- **Prompt:** For SigLIP 2, the bare query (`dog`) beats "a photo of a dog." (F1 0.68 vs. 0.61 with the int8 text model). That's convenient, because it's exactly what the user types.
- **Quantization:** The int8 text model loses accuracy (F1 0.75 → 0.68 with the fp32 vision model). The int8 vision model is 2× faster on the CPU but drops recall from 73% to 55%. The SigLIP 2 text model is large only because of its 256k-token vocabulary; trimming that vocabulary could shrink it a lot.
- **SigLIP scores are absolute:** One threshold (about 0.005–0.01) works across queries, and "giraffe" never lights up. This is the property the research expected, and it held.
- **The region finder matters as much as the model:** On SigLIP, adding regions for flat charts raised large-target recall but cost precision. (Round 1 blamed weak small-target recall on thumbnails merging; round 2 found the real cause was the region cap.)
- **OWL-ViT's real strength is small objects:** On tiles at 0.05 it finds 93% of small targets, though most of its highlights are then wrong. To make it fit as-you-type, it needs an export that splits the image encoder from the query head, so the image runs once per screenshot.
- **Runtime:** `Microsoft.ML.OnnxRuntime.DirectML` stopped at 1.24.4 (DirectML is in maintenance). The fp16 SigLIP vision model fails to load on CPU in ORT 1.24.4 (an optimizer bug) but runs on DirectML.

## Verdict against the bar we set

The bar: at least 80% recall, at most 10% false alarms on absent queries (90% quiet), at most 1 s per
screenshot on a CPU, at most 50 ms per keystroke.

- **SigLIP 2 B/16, walls split, threshold 0.005:** 84% recall, 95% quiet, 13–28 ms per keystroke. Passes everything except CPU time: 2.8 s median on CPU, 0.43 s on this GPU.
- **SigLIP 2 B/32, walls split, threshold 0.002:** 86% recall, 91% quiet, 1.0 s median on CPU. Passes the bar, but only 56% of its highlights are right, against 70% for B/16.
- **OWL-ViT:** Misses on concepts, and on keystroke cost as exported. No longer needed for small objects.

A design that clears the bar: B/16 where a GPU or NPU is available, B/32 on CPU-only PCs. Text hits stay
instant either way, and object and concept hits arrive a fraction of a second later.

**Caveat:** The thresholds were picked on the same 17 screens they are scored on, and the region finder was
tuned while looking at them. Expect real screens to come out somewhat worse. A held-out set of real
screenshots is the next thing to build before trusting these numbers.

Next steps:

1. Held-out test set from real screenshots, to confirm the thresholds and the region finder.
2. NPU timing through the OpenVINO execution provider (could make B/16 viable without a discrete GPU).
3. Shrink the 1.1 GB text model: keep only the vocabulary tokens that common queries need, or test fp16 text.
