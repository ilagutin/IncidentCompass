# Regenerates tests/Shared/fixtures/relevance-judge-model/ from generate.py in a throwaway, pinned
# python:3.12-slim container. The generated files are committed; the tests never run this script.
#
# The whole tools/ directory is mounted rather than this one, because generate.py trains its
# tokenizer on the embedding fixture's training-text.txt: the real judge and the real embedding
# model share one XLM-RoBERTa SentencePiece file, so the fixtures share one training text.
$ErrorActionPreference = "Stop"
$toolsDirectory = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputDirectory = New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot "..\..\tests\Shared\fixtures\relevance-judge-model")

docker run --rm -v "$($toolsDirectory.Path):/tools:ro" -v "$($outputDirectory.FullName):/out" python:3.12-slim@sha256:78387bc3881b8273120a12ebe6c1ab22b018ccc2c9adf565ae1ac9b536e184ea sh -c "pip install --quiet --no-cache-dir --disable-pip-version-check --root-user-action=ignore -r /tools/fixture-relevance-judge-model/requirements.txt && python /tools/fixture-relevance-judge-model/generate.py /out"
exit $LASTEXITCODE
