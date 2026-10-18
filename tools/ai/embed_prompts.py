"""
Builds src/VibePlayer/Assets/ai-prompts.json: CLIP ViT-B/32 text embeddings for the scene prompts.
Run once after changing the prompts (needs: pip install onnxruntime tokenizers numpy):
    python embed_prompts.py <folder with tokenizer.json + text_model.onnx> ../../src/VibePlayer/Assets/ai-prompts.json
Both files are from https://huggingface.co/Xenova/clip-vit-base-patch32 (tokenizer.json, onnx/text_model.onnx).
"""
import json, sys
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

models, out = sys.argv[1], sys.argv[2]

# category -> prompts. "neutral" prompts are the competition in the softmax, so a frame only scores
# high for a category when it looks more like that category than like ordinary film footage.
CATEGORIES = {
    "kiss": [
        "a photo of two people kissing",
        "a close-up of a couple kissing on the lips",
        "a romantic kiss in a movie scene",
        "two people making out passionately",
    ],
    "sex": [
        "a sex scene from a movie",
        "a couple having sex in bed",
        "two naked people in bed together",
        "a couple making love under the sheets",
        "an erotic scene",
    ],
    "nudity": [
        "a photo of a naked person",
        "a topless woman",
        "a nude body",
    ],
    # Ordinary footage. Prompts about content, not style: style prompts ("an old black and white movie")
    # also swallowed real kisses in old films. Checked against fp32 CLIP (the fp16 model agrees; the 8-bit
    # one does not, so it isn't used).
    "neutral": [
        "two people talking",
        "a close-up of a person's face",
        "a man and a woman having a conversation",
        "people hugging",
        "a person sleeping in bed",
        "a family having dinner",
        "people walking on a street",
        "a group of people at a party",
        "people swimming at the beach",
        "a person in a bedroom",
        "a fight scene",
        "a car driving on a road",
        "a landscape",
        "a room interior",
        "text on a screen",
        "a dark scene at night",
        "an animated cartoon",
        "a person getting dressed",
        "a shirtless man",
        "a woman in a swimsuit",
        "children in a classroom",
        "a person bending down",
        "a person covering their head",
        "machines and equipment",
        "soldiers",
        "a close-up of hands",
        "an object",
        "a man and a woman standing side by side",
        "a person looking at the camera",
        "a child",
        "an old man",
        "a woman smiling",
        "a crowd of people",
        "a building",
    ],
}

tok = Tokenizer.from_file(f"{models}/tokenizer.json")
sess = ort.InferenceSession(f"{models}/text_model.onnx", providers=["CPUExecutionProvider"])
result = {"model": "openai/clip-vit-base-patch32 (Xenova ONNX export)", "logitScale": 100.0, "categories": {}}
for cat, prompts in CATEGORIES.items():
    rows = []
    for p in prompts:
        ids = np.array([tok.encode(p).ids], dtype=np.int64)
        emb = sess.run(None, {"input_ids": ids})[0][0]
        emb = emb / np.linalg.norm(emb)
        rows.append({"text": p, "embedding": [round(float(x), 6) for x in emb]})
    result["categories"][cat] = rows
with open(out, "w", encoding="utf-8") as f:
    json.dump(result, f, separators=(",", ":"))
print("wrote", out, sum(len(v) for v in CATEGORIES.values()), "prompts; example ids:", tok.encode(CATEGORIES["kiss"][0]).ids)
