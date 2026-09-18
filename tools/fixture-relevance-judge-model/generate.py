"""Generates the tiny relevance-judge fixture model the LocalOnnx judge adapter tests run against.

The output has the shape of the real BAAI/bge-reranker-v2-m3 artifacts and nothing of their content:

- sentencepiece.bpe.model: a SentencePiece Unigram model trained on the embedding fixture's
  training-text.txt, with the default <unk>=0, <s>=1, </s>=2 layout, so the adapter's fairseq id
  mapping applies unchanged. The two fixtures share one training text on purpose: the real judge
  and the real embedding model share their XLM-RoBERTa SentencePiece file byte for byte.
- model.onnx: a random-weight graph, seeded, with the inputs input_ids, attention_mask and
  token_type_ids and the output logits of shape [batch, 1]. The score depends on every token, its
  position and its token type, so different pairs score differently and one pair scores the same
  on every run. Position embeddings are sliced to the sequence length and added to the token
  vectors, so an input longer than MAX_POSITIONS fails the same way the real model fails past its
  own window.
- wrong-shape-model.onnx: the same graph with an output named logits of shape [batch, sequence]:
  one score per token rather than one per row. It is what the adapter's output-shape refusal is
  tested against; it is never installed through a manifest.
- expected-scores.json: the mapped ids and the score the fixture produces for a few fixed pairs.
- manifest.json: the artifact description in the store's current manifest schema, of kind
  relevance_judge. File paths are relative to the fixture directory and the URLs are placeholders
  that are never fetched.

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
TRAINING_TEXT = TOOL_DIRECTORY.parent / "fixture-embedding-model" / "training-text.txt"
VOCAB_SIZE = 128
HIDDEN_SIZE = 8
MAX_POSITIONS = 48
SEED = 20260917
MODEL_FILE = "model.onnx"
WRONG_SHAPE_MODEL_FILE = "wrong-shape-model.onnx"
TOKENIZER_FILE = "sentencepiece.bpe.model"

# A cross-encoder pair is <s> query </s> </s> passage </s>: four markers around two segments.
SPECIAL_TOKEN_COUNT = 4

EXPECTED_PAIRS = [
    ("checkout timeout", "checkout timeout while calling the payment service"),
    ("checkout timeout", "the office coffee machine is descaled every friday afternoon"),
    ("przekroczono limit czasu", "usluga platnosci odpowiada wolno i zamowienie nie zostalo zlozone"),
    ("  gateway latency  ", "A passage with trailing space "),
    # The passage is cut to what the query leaves.
    ("checkout timeout", "checkout timeout while calling the payment service " * 8),
    # The query alone does not fit, so it is cut too and the passage keeps nothing.
    ("checkout timeout while calling the payment service " * 8, "payment service latency"),
]


def train_tokenizer(output_directory):
    model_prefix = output_directory / "sentencepiece.bpe"
    spm.SentencePieceTrainer.train(
        input=str(TRAINING_TEXT),
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


def expected_ids(processor, query, passage):
    """The pair layout of XLMRobertaTokenizerFast, with this adapter's truncation order.

    The passage is cut to whatever the query leaves, and the query is cut only when it alone does
    not fit. The reference tokenizer's default instead shortens whichever segment is currently
    longer; the two agree for every pair that fits.
    """
    budget = MAX_POSITIONS - SPECIAL_TOKEN_COUNT
    query_ids = [map_sentencepiece_id(piece_id) for piece_id in processor.encode(query.strip())][:budget]
    passage_budget = max(0, budget - len(query_ids))
    passage_ids = [map_sentencepiece_id(piece_id) for piece_id in processor.encode(passage.strip())][:passage_budget]
    return [0] + query_ids + [2, 2] + passage_ids + [2]


def build_graph(vocabulary_rows, one_score_per_row):
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
            rng.standard_normal((HIDDEN_SIZE, 1)).astype(np.float32), "score_weights"),
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
        helper.make_node("Mul", ["activated", "mask_expanded"], ["masked"]),
    ]
    if one_score_per_row:
        nodes.append(helper.make_node("ReduceMean", ["masked"], ["pooled"], axes=[1], keepdims=0))
        nodes.append(helper.make_node("MatMul", ["pooled", "score_weights"], ["logits"]))
        output_shape = ["batch_size", 1]
        graph_name = "incidentcompass_fixture_relevance_judge_model"
    else:
        nodes.append(helper.make_node("ReduceMean", ["masked"], ["logits"], axes=[2], keepdims=0))
        output_shape = ["batch_size", "sequence_length"]
        graph_name = "incidentcompass_fixture_relevance_judge_wrong_shape_model"

    sequence = ["batch_size", "sequence_length"]
    graph = helper.make_graph(
        nodes,
        graph_name,
        [
            helper.make_tensor_value_info("input_ids", TensorProto.INT64, sequence),
            helper.make_tensor_value_info("attention_mask", TensorProto.INT64, sequence),
            helper.make_tensor_value_info("token_type_ids", TensorProto.INT64, sequence),
        ],
        [helper.make_tensor_value_info("logits", TensorProto.FLOAT, output_shape)],
        initializers,
    )
    model = helper.make_model(
        graph,
        producer_name="incidentcompass-fixture-relevance-judge-model",
        opset_imports=[helper.make_opsetid("", 17)],
    )
    model.ir_version = 8
    onnx.checker.check_model(model)
    return model


def run_graph(session, ids):
    input_ids = np.array([ids], dtype=np.int64)
    return session.run(
        ["logits"],
        {
            "input_ids": input_ids,
            "attention_mask": np.ones_like(input_ids),
            "token_type_ids": np.zeros_like(input_ids),
        },
    )[0]


def open_session(model_path):
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    input_names = [value.name for value in session.get_inputs()]
    if input_names != ["input_ids", "attention_mask", "token_type_ids"]:
        raise SystemExit("unexpected graph inputs: " + ", ".join(input_names))
    return session


def self_check(session, wrong_shape_session, sample_ids):
    logits = run_graph(session, sample_ids)
    if logits.shape != (1, 1):
        raise SystemExit("unexpected output shape: " + str(logits.shape))
    wrong = run_graph(wrong_shape_session, sample_ids)
    if wrong.shape != (1, len(sample_ids)):
        raise SystemExit("unexpected wrong-shape output: " + str(wrong.shape))
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
    vocabulary_rows = processor.get_piece_size() + 1
    model_path = output_directory / MODEL_FILE
    wrong_shape_path = output_directory / WRONG_SHAPE_MODEL_FILE
    onnx.save(build_graph(vocabulary_rows, one_score_per_row=True), str(model_path))
    onnx.save(build_graph(vocabulary_rows, one_score_per_row=False), str(wrong_shape_path))

    session = open_session(model_path)
    wrong_shape_session = open_session(wrong_shape_path)
    cases = []
    for query, passage in EXPECTED_PAIRS:
        ids = expected_ids(processor, query, passage)
        cases.append({
            "query": query,
            "passage": passage,
            "ids": ids,
            "score": float(run_graph(session, ids)[0][0]),
        })
    self_check(session, wrong_shape_session, cases[0]["ids"])
    write_json(
        output_directory / "expected-scores.json",
        {"maxTokens": MAX_POSITIONS, "specialTokenCount": SPECIAL_TOKEN_COUNT, "cases": cases},
    )

    write_json(
        output_directory / "manifest.json",
        {
            "schemaVersion": 2,
            "id": "incidentcompass/fixture-relevance-judge-model",
            "revision": "fixture-1",
            "modelFile": {
                "path": MODEL_FILE,
                "url": "https://fixture.invalid/incidentcompass/relevance-judge/" + MODEL_FILE,
                "sha256": sha256_of(model_path),
                "kind": "onnx",
            },
            "tokenizerFile": {
                "path": TOKENIZER_FILE,
                "url": "https://fixture.invalid/incidentcompass/relevance-judge/" + TOKENIZER_FILE,
                "sha256": sha256_of(output_directory / TOKENIZER_FILE),
                "kind": "sentencepiece",
            },
            "maxTokens": MAX_POSITIONS,
            "license": "Apache-2.0",
            "kind": "relevance_judge",
        },
    )


if __name__ == "__main__":
    main()
