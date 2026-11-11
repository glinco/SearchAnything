# Downloads the spike's inputs into models/ and data/photos/ (both git-ignored) and checks every file
# against a pinned SHA-256. Models come from Hugging Face at fixed revisions; photos from Wikimedia
# Commons (see eval/photos.json for each photo's source, author and license).
# This is developer tooling only. SearchAnything itself still has no network code.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$here = $PSScriptRoot

$siglip = 'https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/ba1f3b0843f24bc5417d38e19c37b287d719b2f4'
$siglip32 = 'https://huggingface.co/onnx-community/siglip2-base-patch32-256-ONNX/resolve/7efccb86b5b6601bfe9e14326e1c11b40b68d44c'
$owl = 'https://huggingface.co/Xenova/owlvit-base-patch32/resolve/b75f4e52949639c3bb0b96546ea4149482f6e7ef'
$files = @(
    @{ Url = "$siglip/tokenizer.json"; Path = 'models/siglip2/tokenizer.json'; Sha = 'cb9140fae3ac5122c972d37adf83e1248471a38147ad76f8215c8872c6fd8322' },
    @{ Url = "$siglip/onnx/vision_model.onnx"; Path = 'models/siglip2/onnx/vision_model.onnx'; Sha = 'c0573e3f4140c3a7c4e9cc5912bd6b26a033b46a6a8e8af26cbea262b163bcad' },
    @{ Url = "$siglip/onnx/vision_model_fp16.onnx"; Path = 'models/siglip2/onnx/vision_model_fp16.onnx'; Sha = 'a1959f7bd3993a607e48839f6d01e25b876fe76afda301b028b78eef68aabd95' },
    @{ Url = "$siglip/onnx/vision_model_quantized.onnx"; Path = 'models/siglip2/onnx/vision_model_quantized.onnx'; Sha = '5f2b401c1a4fc095702a5d45348e17ad46c4f87064085365b43c6e8eaa5c0070' },
    @{ Url = "$siglip/onnx/text_model.onnx"; Path = 'models/siglip2/onnx/text_model.onnx'; Sha = 'baf12d941beabafafb14f7b4adb38dc15be18681b964a84410ec53d9d65e6293' },
    @{ Url = "$siglip/onnx/text_model_quantized.onnx"; Path = 'models/siglip2/onnx/text_model_quantized.onnx'; Sha = '3a0603d3a00c05a80a6ded4743c16aaac7b1e62cdcc7e362e7ce418659b96400' },
    @{ Url = "$siglip32/tokenizer.json"; Path = 'models/siglip2-b32/tokenizer.json'; Sha = 'cb9140fae3ac5122c972d37adf83e1248471a38147ad76f8215c8872c6fd8322' },
    @{ Url = "$siglip32/onnx/vision_model.onnx"; Path = 'models/siglip2-b32/onnx/vision_model.onnx'; Sha = '0ddb7507cfcb54c2cd503029949add9dc6586de67164bb69c2c09588cdcc9719' },
    @{ Url = "$siglip32/onnx/vision_model_fp16.onnx"; Path = 'models/siglip2-b32/onnx/vision_model_fp16.onnx'; Sha = '44fcd984cf4ac4baba5b4776192c0b141a120c6981df49732c681d4fe8ddc718' },
    @{ Url = "$siglip32/onnx/text_model.onnx"; Path = 'models/siglip2-b32/onnx/text_model.onnx'; Sha = 'e8950ceb970ea302be3559055df99af8a1c5e1bf5a25e34d4ed0069340a3c8aa' },
    @{ Url = "$owl/vocab.json"; Path = 'models/owlvit/vocab.json'; Sha = '5047b556ce86ccaf6aa22b3ffccfc52d391ea4accdab9c2f2407da5b742d4363' },
    @{ Url = "$owl/merges.txt"; Path = 'models/owlvit/merges.txt'; Sha = '9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a' },
    @{ Url = "$owl/onnx/model.onnx"; Path = 'models/owlvit/onnx/model.onnx'; Sha = '38d7e8b11298ab99fc5929d785062baffca296c2bf9976b050c4fa68802c6b80' },
    @{ Url = "$owl/onnx/model_quantized.onnx"; Path = 'models/owlvit/onnx/model_quantized.onnx'; Sha = '06af49bd5db977936bcddad2b45d0031072673517579d15b8cb42bf015a94156' }
)
# Assign first: Windows PowerShell's ConvertFrom-Json emits the whole array as one pipeline object.
$photos = Get-Content (Join-Path $here 'eval/photos.json') -Raw | ConvertFrom-Json
foreach ($photo in $photos) {
    $files += @{ Url = $photo.url; Path = "data/photos/$($photo.file)"; Sha = $photo.sha256 }
}

$headers = @{ 'User-Agent' = 'SearchAnything-eval/0.1 (https://github.com/glinco/SearchAnything)' }
foreach ($f in $files) {
    $path = Join-Path $here $f.Path
    if ((Test-Path $path) -and (Get-FileHash $path -Algorithm SHA256).Hash -eq $f.Sha) { continue }
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    Write-Host "Downloading $($f.Path)"
    Invoke-WebRequest -Uri $f.Url -OutFile $path -Headers $headers -UseBasicParsing
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($hash -ne $f.Sha) {
        Remove-Item $path
        throw "Hash mismatch for $($f.Path): got $hash"
    }
}
Write-Host "All $($files.Count) files present and verified."
