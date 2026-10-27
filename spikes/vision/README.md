# Visual search spike

**Question:** Can a small model running on your own PC find things like "dog" or "chart" on a screenshot,
accurately and fast enough for search-as-you-type? This is the first item on the roadmap.

This folder is an experiment, not part of the app. It isn't in `SearchAnything.slnx`, the app still has no
network code and no models, and nothing here changes what SearchAnything promises in [SECURITY.md](../../SECURITY.md).

## Try it

You need the .NET 10 SDK and Microsoft Edge. The first step downloads about 4 GB.

```powershell
.\spikes\vision\fetch.ps1           # models and test photos, each checked against a pinned SHA-256
.\spikes\vision\eval\render.ps1     # renders the 17 test screens
cd spikes\vision\VisionSpike

# Search a screenshot of your own (add --gpu if you have one)
dotnet run -c Release -- try --image C:\path\to\screenshot.png dog chart "coffee cup"

# Score a model on the test screens
dotnet run -c Release -- eval --variant b16 --gpu --draw
```

`try` prints every highlight with its score and saves a copy of the image with the highlights drawn on it to
`results/`, which git ignores. Run with no arguments for every option.

## How it works

1. **Find candidate regions.** OCR (the app's own `OcrService`) marks where text is. Small blocks with
   visible texture that aren't text are grouped into areas: photos, charts, maps, QR codes. Thumbnail walls
   are cut along their gutters, one region per thumbnail. A coarse grid covers everything else, for mostly-text
   concepts like "table".
2. **Describe each region once per screenshot.** SigLIP 2's image encoder turns each region into a list of
   768 numbers (an *embedding*).
3. **Describe the query on every keystroke.** The text encoder turns "dog" into the same kind of list. That's
   the only per-keystroke work.
4. **Compare.** SigLIP is trained so the comparison gives a probability on its own, so one threshold works for
   every query, and absent things ("giraffe") stay dark.

OWL-ViT, an object detector that draws its own boxes, was tested as the alternative.

## How it's scored

17 rendered 1920×1080 screens (gallery, article, shop, dashboard, slides, chat, inbox, map, code editor,
video, calendar, social feed, spreadsheet, desktop, scanned PDF, and two thumbnail walls) built from 80
Wikimedia Commons photos plus generated charts, tables and UI. Every target's box is known. All 28 queries
run on every screen; three of them (giraffe, guitar, fire truck) never appear.

- **Recall:** targets highlighted. Small means under 120 px on a side.
- **Precision:** highlights that are right. A highlight is right if most of it sits on a target.
- **Quiet:** screens without the thing where nothing lights up.

## Results

Core Ultra 7 258V with Arc 140V graphics. "Per screenshot" includes OCR and finding regions.

| | SigLIP 2 B/16 | SigLIP 2 B/32 | OWL-ViT B/32 (6 tiles) |
|---|---|---|---|
| Threshold | 0.005 | 0.002 | 0.15 |
| Recall (small / medium / large) | **82%** (81 / 82 / 89) | **84%** (86 / 82 / 89) | 73% (62 / 89 / 28) |
| Precision | 69% | 56% | 68% |
| Quiet when absent | 95% | 91% | 93% |
| Top highlight right, when present | 94% | 93% | 87% |
| Concepts (chart, table, map, code) | found | found | mostly missed |
| Per screenshot, GPU / CPU | 0.50 s / 3.1 s | 0.25 s / 1.1 s | (all per keystroke) |
| Per keystroke, GPU / CPU | 13 ms / 30 ms | 14 ms / 30 ms | 0.68 s / about 1.6 s |
| Download | 0.2–0.4 GB image + 1.1 GB text | same | 0.6 GB |

The bar set before testing: at least 80% recall, at least 90% quiet, at most 1 s per screenshot on a CPU,
at most 50 ms per keystroke.

- **B/16** passes everything except CPU time: 3.1 s, against 0.5 s on the GPU.
- **B/32** passes everything except CPU time, which is just over at 1.1 s. It makes more wrong highlights.
- **OWL-ViT** misses on concepts and on keystroke cost as exported.

**Decision:** SigLIP 2 is the foundation. Use B/16 where there's a GPU or NPU, and B/32 elsewhere. Text hits
stay instant either way; object and concept hits follow a fraction of a second later.

## What changed the numbers

| Change | Effect |
|---|---|
| Query as-is (`dog`) instead of "a photo of a dog." | F1 0.61 → 0.68. Convenient: it's what the user types. |
| 8-bit text model instead of full precision | Loses accuracy (F1 0.75 → 0.68), so full precision is used |
| 8-bit image model | 2× faster on CPU, but recall dropped from 73% to 55% |
| Region cap raised from 64 to 128 | Small-target recall 61% → 82%. The cap had been cutting off most of a 78-thumbnail wall. |
| Cut thumbnail walls along their gutters | Medium-target recall 51% → 86%, on walls whose 3 px gutters otherwise merge into one region |
| B/32 at 256 px instead of B/16 at 224 px | About 2.5× less work per region, lower accuracy |

One wrong turn is worth recording: the first round blamed the small-target misses on thumbnails merging
together. The real cause was the region cap.

## Limitations

- **The numbers are optimistic.** Thresholds and the region finder were tuned on the same 17 screens they're
  scored on. Real screenshots will do somewhat worse; a held-out set of real screenshots is the next step.
- **Highlights can be coarse.** On a big photo, the best-scoring regions are sometimes grid squares inside
  it, so the highlight shows two or four squares instead of the photo's outline.
- **Chart types get confused.** "Bar chart" also lights up line and pie charts.
- **The text model is 1.1 GB,** almost all of it a 256,000-word vocabulary. Trimming it is future work.
- **Runtime:** the DirectML build of ONNX Runtime is frozen at 1.24.4, and it can't load the fp16 image
  model on the CPU (an optimizer bug), so the CPU uses fp32.

## What's here

| Path | What it is |
|---|---|
| `fetch.ps1` | Downloads models (Hugging Face, pinned revisions) and photos, checking each file's SHA-256 |
| `eval/scenes.html`, `eval/render.ps1` | Builds the test screens and records each target's box. The filler text is random words on purpose: the test is about pictures, and real sentences could match queries. |
| `eval/photos.json` | Each photo's labels, source, author and license |
| `VisionSpike/Search.cs` | The two approaches behind one interface: prepare once per screenshot, then find per keystroke |
| `VisionSpike/Proposals.cs` | Region finder and thumbnail-wall splitting |
| `VisionSpike/Models.cs`, `Tokenizers.cs`, `ImageOps.cs` | Running the ONNX models, turning text into token IDs (matches Hugging Face's output), resizing crops |
| `VisionSpike/Metrics.cs` | Scoring against the ground truth |

Licenses: SigLIP 2 and OWL-ViT are Apache 2.0. Photos are fetched, not committed; most are CC BY or CC BY-SA.
