import json
import urllib.request
import random

wf_path = r"D:\AI\ComfyUI-aki-v1.6\ComfyUI\user\default\workflows\艾芙.json"
with open(wf_path, "r", encoding="utf-8") as f:
    ui = json.load(f)

with urllib.request.urlopen("http://127.0.0.1:8188/object_info", timeout=30) as r:
    object_info = json.loads(r.read())

SKIP = {
    "Note", "Reroute", "PrimitiveNode",
    "Anything Everywhere", "Anything Everywhere3", "Anything Everywhere?",
    "Prompts Everywhere", "Seed Everywhere", "Simple String",
}
CONTROL = {"fixed", "increment", "decrement", "randomize"}

links = {}
for link in ui.get("links") or []:
    if len(link) >= 5:
        links[link[0]] = (link[1], link[2])

ue_map = {}
for ue in (ui.get("extra") or {}).get("ue_links") or []:
    try:
        down = int(ue["downstream"])
        dslot = int(ue["downstream_slot"])
        up = int(ue["upstream"])
        uslot = int(ue.get("upstream_slot") or 0)
        ue_map[(down, dslot)] = (up, uslot)
    except Exception:
        pass


def is_conn_only(class_type, name):
    info = object_info.get(class_type)
    if not info:
        return False
    inp = info.get("input") or {}
    defn = None
    for sec in ("required", "optional", "hidden"):
        if name in (inp.get(sec) or {}):
            defn = inp[sec][name]
            if sec == "hidden":
                return True
            break
    if not defn:
        return False
    t = defn[0]
    if isinstance(t, list):
        return False
    if t in ("INT", "FLOAT", "STRING", "BOOLEAN"):
        return False
    if t in ("MODEL", "CLIP", "VAE", "CONDITIONING", "LATENT", "IMAGE", "MASK", "CONTROL_NET", "*"):
        return True
    if isinstance(t, str) and t.isupper() and t not in ("INT",):
        return True
    return False


def widget_order(class_type, fallback):
    info = object_info.get(class_type)
    if not info:
        return fallback
    order = []
    io = info.get("input_order") or {}
    for sec in ("required", "optional"):
        for name in io.get(sec) or []:
            if is_conn_only(class_type, name):
                continue
            order.append(name)
    if not order:
        return fallback
    s = set(order)
    for f in fallback:
        if f not in s and not is_conn_only(class_type, f):
            order.append(f)
    return order


def is_token_json(v):
    return isinstance(v, str) and v.lstrip().startswith('[{"id"')


prompt = {}
for node in ui.get("nodes") or []:
    nid = node.get("id")
    mode = node.get("mode", 0)
    if mode in (2, 4):
        continue
    ct = node.get("type") or ""
    if not ct or ct in SKIP:
        continue
    inputs_obj = {}
    node_inputs = node.get("inputs") or []
    widgets = node.get("widgets_values") or []
    widget_names = []
    for idx, inp in enumerate(node_inputs):
        name = inp.get("name")
        if not name:
            continue
        if inp.get("widget") is not None:
            widget_names.append(name)
        link = inp.get("link")
        if link is not None and link in links:
            src, slot = links[link]
            inputs_obj[name] = [str(src), slot]
    for (down, dslot), (up, uslot) in ue_map.items():
        if down != nid:
            continue
        if 0 <= dslot < len(node_inputs):
            name = node_inputs[dslot].get("name")
            if name and name not in inputs_obj:
                inputs_obj[name] = [str(up), uslot]
    ordered = widget_order(ct, widget_names)
    wi = 0
    for name in ordered:
        if name in inputs_obj:
            continue
        if wi >= len(widgets):
            break
        val = widgets[wi]
        if isinstance(val, str) and val in CONTROL:
            wi += 1
            if wi >= len(widgets):
                break
            val = widgets[wi]
        wi += 1
        if is_token_json(val):
            continue
        if val is None:
            continue
        inputs_obj[name] = val
    if ct == "ZmlPowerLoraLoader":
        if "lora_loader_data" not in inputs_obj:
            data = None
            props = node.get("properties") or {}
            if "powerLoraLoader_data" in props:
                data = json.dumps(props["powerLoraLoader_data"], ensure_ascii=False)
            elif widgets:
                data = widgets[0] if isinstance(widgets[0], str) else json.dumps(widgets[0], ensure_ascii=False)
            if data:
                inputs_obj["lora_loader_data"] = data
    if ct == "WeiLinPromptUI":
        if "positive" not in inputs_obj and widgets:
            if not is_token_json(widgets[0]):
                inputs_obj["positive"] = widgets[0]
        if "auto_random" not in inputs_obj and len(widgets) > 1:
            inputs_obj["auto_random"] = widgets[1]
    prompt[str(nid)] = {"class_type": ct, "inputs": inputs_obj}

best = None
bestlen = -1
for k, v in prompt.items():
    if v["class_type"] == "WeiLinPromptUI":
        t = str(v["inputs"].get("positive", ""))
        if len(t) > bestlen:
            bestlen = len(t)
            best = k
print("positive node", best, "len", bestlen)
prompt[best]["inputs"]["positive"] = "masterpiece, best quality, 1girl, simple test, solo"

for k, v in prompt.items():
    if v["class_type"] == "ZML_PresetResolutionV2":
        v["inputs"]["预设"] = "自定义"
        v["inputs"]["自定义宽"] = 512
        v["inputs"]["自定义高"] = 768
        print("res node", k)

for k, v in prompt.items():
    if "seed" in v["inputs"]:
        v["inputs"]["seed"] = random.randint(0, 2**31)
        print("seed node", k, v["inputs"]["seed"])

for k, v in sorted(prompt.items(), key=lambda x: int(x[0])):
    keys = list(v["inputs"].keys())
    print(f"node {k} {v['class_type']}: {keys}")

body = json.dumps({"prompt": prompt, "client_id": "test-alife"}).encode("utf-8")
req = urllib.request.Request(
    "http://127.0.0.1:8188/prompt",
    data=body,
    headers={"Content-Type": "application/json"},
)
try:
    with urllib.request.urlopen(req, timeout=30) as r:
        resp = json.loads(r.read())
    print("SUBMIT OK", resp.get("prompt_id"), "number", resp.get("number"))
except Exception as e:
    if hasattr(e, "read"):
        err = e.read().decode("utf-8", errors="replace")
        print("SUBMIT FAIL", e)
        print(err[:3000])
    else:
        print("SUBMIT FAIL", e)

outp = r"d:\Alife\Alife.Client\Storage\Plugins\Alife.Plugin.Comfyui\workflows\艾芙.api.converted.json"
with open(outp, "w", encoding="utf-8") as f:
    json.dump(prompt, f, ensure_ascii=False, indent=2)
print("saved", outp)
