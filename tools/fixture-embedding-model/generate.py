"""Generates the tiny embedding fixture model the LocalOnnx adapter tests run against.

The output has the shape of the real multilingual E5 artifacts and nothing of their content:

- sentencepiece.bpe.model: a SentencePiece Unigram model trained on training-text.txt, with the
  default <unk>=0, <s>=1, </s>=2 layout, so the adapter's fairseq id mapping applies unchanged.
- model.onnx: a random-weight graph, seeded, with the inputs input_ids, attention_mask and
  token_type_ids and the output last_hidden_state. The output depends on the token, its position
  and its token type. Position embeddings are sliced to the sequence length and added to the token
  vectors, so an input longer than MAX_POSITIONS fails with the same broadcast error the real
  model raises past 512 tokens.
- expected-ids.json: the mapped ids the adapter must produce for a few fixed inputs. Ids only,
  never vector values.
- manifest.json: the artifact description in the store's manifest schema. File paths are
  relative to the fixture directory and the URLs are placeholders that are never fetched.

Run it through generate.ps1, which uses a pinned python:3.12-slim image.
"""

import hashlib
import json
import pathlib
import sys

import numpy as np
import onnx
import onnxruntime as ort
import sentencepiece as spm
from onnx import TensorProto, helper, numpy_helper

TOOL_DIRECTORY = pathlib.Path(__file__).resolve().parent
VOCAB_SIZE = 128
HIDDEN_SIZE = 8
MAX_POSITIONS = 48
SEED = 20260913
QUERY_PREFIX = "query: "
PASSAGE_PREFIX = "passage: "
MODEL_FILE = "model.onnx"
TOKENIZER_FILE = "sentencepiece.bpe.model"

EXPECTED_INPUTS = [
    ("Query", "checkout timeout while calling the payment service"),
    ("Passage", "checkout timeout while calling the payment service"),
    ("Passage", "Przekroczono limit czasu podczas finalizacji zamówienia"),
    ("Query", "Превышено время ожидания при оформлении заказа"),
    ("Query", "  gateway latency 😀  "),
    ("Passage", "checkout timeout " * 40),
]


def train_tokenizer(output_directory):
    model_prefix = output_directory / "sentencepiece.bpe"
    spm.SentencePieceTrainer.train(
        input=str(TOOL_DIRECTORY / "training-text.txt"),
        model_prefix=str(model_prefix),
        model_type="unigram",
        vocab_size=VOCAB_SIZE,
        hard_vocab_limit=False,
        character_coverage=1.0,
        # Identity normalization keeps the model a few kilobytes; the NFKC table alone is ~230 KB.
        # Normalization parity with the real model is proven against the real tokenizer file.
        normalization_rule_name="identity",
        unk_id=0,
        bos_id=1,
        eos_id=2,
        pad_id=-1,
        num_threads=1,
        minloglevel=2,
    )
    (output_directory / "sentencepiece.bpe.vocab").unlink()
    return spm.SentencePieceProcessor(model_file=str(output_directory / TOKENIZER_FILE))


def map_sentencepiece_id(piece_id):
    return {0: 3, 1: 0, 2: 2}[piece_id] if piece_id < 3 else piece_id + 1


def expected_ids(processor, kind, text):
    prefix = QUERY_PREFIX if kind == "Query" else PASSAGE_PREFIX
    piece_ids = processor.encode(prefix + text.strip())
    mapped = [map_sentencepiece_id(piece_id) for piece_id in piece_ids]
    return [0] + mapped[: MAX_POSITIONS - 2] + [2], len(mapped)


def build_graph(vocabulary_rows):
    rng = np.random.default_rng(SEED)
    initializers = [
        numpy_helper.from_array(
            rng.standard_normal((vocabulary_rows, HIDDEN_SIZE)).astype(np.float32), "word_embeddings"),
        numpy_helper.from_array(
            rng.standard_normal((MAX_POSITIONS, HIDDEN_SIZE)).astype(np.float32), "position_embeddings"),
        numpy_helper.from_array(
            rng.standard_normal((2, HIDDEN_SIZE)).astype(np.float32), "token_type_embeddings"),
        numpy_helper.from_array(
            rng.standard_normal((HIDDEN_SIZE, HIDDEN_SIZE)).astype(np.float32), "projection"),
        numpy_helper.from_array(
            np.arange(MAX_POSITIONS, dtype=np.int64).reshape(1, MAX_POSITIONS), "position_ids"),
        numpy_helper.from_array(np.array([0], dtype=np.int64), "zero"),
        numpy_helper.from_array(np.array([1], dtype=np.int64), "one"),
        numpy_helper.from_array(np.array([2], dtype=np.int64), "two"),
    ]
    nodes = [
        helper.make_node("Shape", ["input_ids"], ["input_shape"]),
        helper.make_node("Slice", ["input_shape", "one", "two"], ["sequence_length"]),
        helper.make_node("Slice", ["position_ids", "zero", "sequence_length", "one"], ["sliced_position_ids"]),
        helper.make_node("Gather", ["word_embeddings", "input_ids"], ["token_vectors"]),
        helper.make_node("Gather", ["position_embeddings", "sliced_position_ids"], ["position_vectors"]),
        helper.make_node("Gather", ["token_type_embeddings", "token_type_ids"], ["token_type_vectors"]),
        helper.make_node("Add", ["token_vectors", "position_vectors"], ["positioned"]),
        helper.make_node("Add", ["positioned", "token_type_vectors"], ["embedded"]),
        helper.make_node("MatMul", ["embedded", "projection"], ["projected"]),
        helper.make_node("Tanh", ["projected"], ["activated"]),
        helper.make_node("Cast", ["attention_mask"], ["mask_float"], to=TensorProto.FLOAT),
        helper.make_node("Unsqueeze", ["mask_float", "two"], ["mask_expanded"]),
        helper.make_node("Mul", ["activated", "mask_expanded"], ["last_hidden_state"]),
    ]
    sequence = ["batch_size", "sequence_length"]
    graph = helper.make_graph(
        nodes,
        "incidentcompass_fixture_embedding_model",
        [
            helper.make_tensor_value_info("input_ids", TensorProto.INT64, sequence),
            helper.make_tensor_value_info("attention_mask", TensorProto.INT64, sequence),
            helper.make_tensor_value_info("token_type_ids", TensorProto.INT64, sequence),
        ],
        [helper.make_tensor_value_info("last_hidden_state", TensorProto.FLOAT, sequence + [HIDDEN_SIZE])],
        initializers,
    )
    model = helper.make_model(
        graph,
        producer_name="incidentcompass-fixture-embedding-model",
        opset_imports=[helper.make_opsetid("", 17)],
    )
    model.ir_version = 8
    onnx.checker.check_model(model)
    return model


def run_graph(session, ids):
    input_ids = np.array([ids], dtype=np.int64)
    return session.run(
        ["last_hidden_state"],
        {
            "input_ids": input_ids,
            "attention_mask": np.ones_like(input_ids),
            "token_type_ids": np.zeros_like(input_ids),
        },
    )[0]


def self_check(model_path, sample_ids):
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    input_names = [value.name for value in session.get_inputs()]
    if input_names != ["input_ids", "attention_mask", "token_type_ids"]:
        raise SystemExit("unexpected graph inputs: " + ", ".join(input_names))
    hidden = run_graph(session, sample_ids)
    if hidden.shape != (1, len(sample_ids), HIDDEN_SIZE):
        raise SystemExit("unexpected output shape: " + str(hidden.shape))
    try:
        run_graph(session, [0] + [5] * MAX_POSITIONS + [2])
    except Exception:  # noqa: BLE001 - any runtime failure is the behaviour being checked
        return
    raise SystemExit("the fixture graph accepted an input longer than MAX_POSITIONS")


def sha256_of(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def main():
    output_directory = pathlib.Path(sys.argv[1])
    output_directory.mkdir(parents=True, exist_ok=True)
    processor = train_tokenizer(output_directory)
    model_path = output_directory / MODEL_FILE
    onnx.save(build_graph(processor.get_piece_size() + 1), str(model_path))

    cases = []
    for kind, text in EXPECTED_INPUTS:
        ids, content_length = expected_ids(processor, kind, text)
        cases.append({"kind": kind, "input": text, "contentIdCount": content_length, "ids": ids})
    self_check(model_path, cases[0]["ids"])
    write_json(output_directory / "expected-ids.json", {"maxTokens": MAX_POSITIONS, "cases": cases})

    write_json(
        output_directory / "manifest.json",
        {
            "schemaVersion": 1,
            "id": "incidentcompass/fixture-embedding-model",
            "revision": "fixture-1",
            "modelFile": {
                "path": MODEL_FILE,
                "url": "https://fixture.invalid/incidentcompass/" + MODEL_FILE,
                "sha256": sha256_of(model_path),
                "kind": "onnx",
            },
            "tokenizerFile": {
                "path": TOKENIZER_FILE,
                "url": "https://fixture.invalid/incidentcompass/" + TOKENIZER_FILE,
                "sha256": sha256_of(output_directory / TOKENIZER_FILE),
                "kind": "sentencepiece",
            },
            "dimensions": HIDDEN_SIZE,
            "maxTokens": MAX_POSITIONS,
            "pooling": "mean",
            "normalize": True,
            "queryPrefix": QUERY_PREFIX,
            "passagePrefix": PASSAGE_PREFIX,
            "license": "Apache-2.0",
        },
    )


if __name__ == "__main__":
    main()
