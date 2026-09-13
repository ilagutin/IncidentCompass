# Regenerates tests/Shared/fixtures/embedding-model/ from generate.py in a throwaway, pinned
# python:3.12-slim container. The generated files are committed; the tests never run this script.
$ErrorActionPreference = "Stop"
$outputDirectory = New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot "..\..\tests\Shared\fixtures\embedding-model")

docker run --rm -v "${PSScriptRoot}:/tool:ro" -v "$($outputDirectory.FullName):/out" python:3.12-slim@sha256:78387bc3881b8273120a12ebe6c1ab22b018ccc2c9adf565ae1ac9b536e184ea sh -c "pip install --quiet --no-cache-dir --disable-pip-version-check --root-user-action=ignore -r /tool/requirements.txt && python /tool/generate.py /out"
exit $LASTEXITCODE
