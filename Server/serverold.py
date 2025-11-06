from flask import Flask, request, jsonify, send_from_directory, render_template_string
import os
import urllib.parse
from urllib.parse import unquote_plus

app = Flask(__name__)

# Define base folders for vessels and flags
UPLOAD_FOLDER_VESSELS = os.path.join(os.path.dirname(__file__), 'vessels')
UPLOAD_FOLDER_FLAGS = os.path.join(os.path.dirname(__file__), 'flags')
os.makedirs(UPLOAD_FOLDER_VESSELS, exist_ok=True)  # Ensure vessel folder exists
os.makedirs(UPLOAD_FOLDER_FLAGS, exist_ok=True)  # Ensure flag folder exists

@app.route('/')
def index():
    players = {}
    for user_folder in os.listdir(UPLOAD_FOLDER_VESSELS):
        user_path = os.path.join(UPLOAD_FOLDER_VESSELS, user_folder)
        if os.path.isdir(user_path):
            players[user_folder] = os.listdir(user_path)

    html_template = """
    <!DOCTYPE html>
    <html>
    <head>
        <title>SVIO - Vessel and Flag Management</title>
        <style>
            table { border-collapse: collapse; width: 100%; }
            th, td { border: 1px solid black; padding: 8px; text-align: left; }
            th { background-color: #f2f2f2; }
            button { cursor: pointer; padding: 5px 10px; }
        </style>
    </head>
    <body>
        <h1>SVIO - Vessel and Flag Management</h1>
        <h2>Vessels</h2>
        <table>
            <thead>
                <tr>
                    <th>User</th>
                    <th>Vessel</th>
                    <th>Actions</th>
                </tr>
            </thead>
            <tbody>
                {% for user, vessels in players.items() %}
                    {% for vessel in vessels %}
                    <tr>
                        <td>{{ user }}</td>
                        <td>{{ vessel }}</td>
                        <td>
                            <button onclick="deleteVessel('{{ user }}', '{{ vessel | urlencode }}')">Delete</button>
                        </td>
                    </tr>
                    {% endfor %}
                {% endfor %}
            </tbody>
        </table>
        <script>
            function deleteVessel(user, vessel) {
                fetch(`/vessels/${user}/${vessel}`, { method: 'DELETE' })
                    .then(response => response.text())
                    .then(data => {
                        alert(data);
                        location.reload(); // Reload the page to reflect the changes
                    })
                    .catch(error => console.error('Error:', error));
            }
        </script>

        <h2>Flags</h2>
        <ul id="flags"></ul>
        <script>
            fetch('/flags')
                .then(response => response.json())
                .then(flags => {
                    const flagsList = document.getElementById('flags');
                    flags.forEach(flag => {
                        const li = document.createElement('li');
                        li.textContent = flag;
                        flagsList.appendChild(li);
                    });
                })
                .catch(error => console.error('Error fetching flags:', error));
        </script>
    </body>
    </html>
    """
    return render_template_string(html_template, players=players)

@app.route('/vessels', methods=['GET'])
def list_vessels():
    players = {}
    for user_folder in os.listdir(UPLOAD_FOLDER_VESSELS):
        user_path = os.path.join(UPLOAD_FOLDER_VESSELS, user_folder)
        if os.path.isdir(user_path):
            players[user_folder] = os.listdir(user_path)

    # Generate a plain text response
    response = ";".join(f"{user}:{','.join(vessels)}" for user, vessels in players.items())
    return response

@app.route('/upload/<user>', methods=['POST'])
def upload_file(user):
    user_folder = os.path.join(UPLOAD_FOLDER_VESSELS, user)
    os.makedirs(user_folder, exist_ok=True)

    if 'file' not in request.files:
        return 'No file part', 400
    file = request.files['file']
    if file.filename == '':
        return 'No selected file', 400
    file.save(os.path.join(user_folder, file.filename))
    return 'File uploaded successfully', 200

@app.route('/vessels/<user>/<filename>', methods=['GET'])
def download_file(user, filename):
    user_folder = os.path.join(UPLOAD_FOLDER_VESSELS, user)
    decoded_filename = urllib.parse.unquote_plus(filename)  # Decode the filename
    try:
        return send_from_directory(user_folder, decoded_filename, as_attachment=True)
    except FileNotFoundError:
        return 'File not found', 404

@app.route('/vessels/<user>/<filename>', methods=['DELETE'])
def delete_vessel(user, filename):
    user_folder = os.path.join(UPLOAD_FOLDER_VESSELS, user)
    decoded_filename = urllib.parse.unquote_plus(filename)  # Decode the filename
    file_path = os.path.join(user_folder, decoded_filename)
    try:
        if os.path.exists(file_path):
            os.remove(file_path)
            return 'File deleted successfully', 200
        else:
            return 'File not found', 404
    except Exception as e:
        return f"Error deleting file: {str(e)}", 500

@app.route('/flags/<user>', methods=['POST'])
def upload_flag(user):
    user_folder = os.path.join(UPLOAD_FOLDER_FLAGS, user)
    os.makedirs(user_folder, exist_ok=True)  # Ensure user folder exists

    if 'file' not in request.files:
        return 'No file part', 400

    file = request.files['file']
    if file.filename == '':
        return 'No selected file', 400

    file_path = os.path.join(user_folder, file.filename)
    file.save(file_path)  # Save the uploaded flag file

    return 'Flag uploaded successfully', 200

@app.route('/flags', methods=['GET'])
def list_flags():
    flags = []
    for user_folder in os.listdir(UPLOAD_FOLDER_FLAGS):
        user_path = os.path.join(UPLOAD_FOLDER_FLAGS, user_folder)
        if os.path.isdir(user_path):
            for flag_file in os.listdir(user_path):
                flags.append(f"{user_folder}/{flag_file}")

    return ";".join(flags)  # Return as semicolon-separated list


@app.route('/flags/<user>/<filename>', methods=['GET'])
def download_flag(user, filename):
    user_folder = os.path.join(UPLOAD_FOLDER_FLAGS, user)
    decoded_filename = urllib.parse.unquote(filename)  # Decode the filename
    try:
        return send_from_directory(user_folder, decoded_filename, as_attachment=True)
    except FileNotFoundError:
        return 'Flag not found', 404
        
@app.route('/flags/<user>/<filename>', methods=['DELETE'])
def delete_flag(user, filename):
    user_folder = os.path.join(UPLOAD_FOLDER_FLAGS, user)
    decoded_filename = urllib.parse.unquote(filename)
    try:
        os.remove(os.path.join(user_folder, decoded_filename))
        return 'Flag file deleted', 200
    except FileNotFoundError:
        return 'Flag file not found', 404




# ----------------------------------------------------
# SCENARIO MODULE SYNCING (persistent.sfs format)
# ----------------------------------------------------
# ---- SCANsat helpers -------------------------------------------------
import base64, glob

def _b64decode(s: str) -> bytes:
    """Standard Base64 (SCANsat uses normal +/ and =) with forgiving padding."""
    s = (s or "").strip()
    pad = "=" * ((4 - (len(s) % 4)) % 4)
    return base64.b64decode(s + pad)

def _b64encode(b: bytes) -> str:
    """Standard Base64, keep '=' padding (SCANsat happily accepts it)."""
    return base64.b64encode(b).decode("ascii")

def _map_union(a: str, b: str) -> str:
    """Bitwise OR of two SCANsat coverage maps (byte arrays)."""
    try:
        A, B = _b64decode(a), _b64decode(b)
        L = max(len(A), len(B))
        if len(A) < L: A += b"\x00" * (L - len(A))
        if len(B) < L: B += b"\x00" * (L - len(B))
        merged = bytes(x | y for x, y in zip(A, B))
        return _b64encode(merged)
    except Exception:
        # Fallback (graceful but lossy) if someone uploaded non-base64
        return a if len(a) >= len(b) else b

def _extract_map(block: str) -> str:
    for ln in (block or "").splitlines():
        t = ln.strip()
        if t.startswith("Map ="):
            return t.split("=", 1)[1].strip()
    return ""

def _replace_map(block: str, new_map: str) -> str:
    out, replaced = [], False
    for ln in (block or "").splitlines():
        if not replaced and ln.strip().startswith("Map ="):
            indent = ln[:len(ln) - len(ln.lstrip())]
            out.append(f"{indent}Map = {new_map}")
            replaced = True
        else:
            out.append(ln)
    if not replaced:
        # insert before closing brace to keep SCANsat happy
        for i in range(len(out) - 1, -1, -1):
            if out[i].strip() == "}":
                out.insert(i, f"    Map = {new_map}")
                break
    return "\n".join(out)

def _blocks_by(prefix: str, text: str):
    """Collect blocks keyed by 'guid' (Vessel) or 'Name' (Body)."""
    out, cur, key, lvl = {}, [], None, 0
    for raw in (text or "").splitlines():
        s = raw.strip()
        if s.startswith(prefix):
            cur, key, lvl = [raw.rstrip()], None, 1
        elif cur:
            cur.append(raw.rstrip())
            lvl += raw.count("{") - raw.count("}")
            if lvl <= 0:
                # finalize
                for ln in cur:
                    t = ln.strip()
                    if prefix == "Vessel" and t.startswith("guid ="):
                        key = t.split("=", 1)[1].strip()
                    if prefix == "Body" and t.startswith("Name ="):
                        key = t.split("=", 1)[1].strip()
                if key:
                    out[key] = "\n".join(cur)
                cur, key = [], None
    return out

def _single_block(name: str, text: str):
    """Return a single named block like SCANResources ... } (or None)."""
    cur, lvl, inside = [], 0, False
    for raw in (text or "").splitlines():
        s = raw.strip()
        if not inside and s.startswith(name):
            inside = True
        if inside:
            cur.append(raw.rstrip())
            lvl += raw.count("{") - raw.count("}")
            if lvl <= 0:
                return "\n".join(cur)
    return None

def _merge_header_flags(text: str, agg: dict):
    """OR some booleans across users (purely UI; doesn’t affect coverage)."""
    # keys vary by SCANsat versions; accept a few common ones
    keys = {
        "storageUpgraded", "mainMapVisible", "bigMapVisible",
        "zoomMapLegend", "overlay", "mainMap", "bigMap", "zoomMap"
    }
    for ln in (text or "").splitlines():
        s = ln.strip()
        if "=" in s:
            k, v = [x.strip() for x in s.split("=", 1)]
            if k in keys:
                agg[k] = agg.get(k, False) or (v.lower() in ("true", "1", "yes"))
    return agg

def _render_header_flags(flags: dict):
    lines = []
    for k, v in flags.items():
        lines.append(f"\t\t{k} = {'True' if v else 'False'}")
    return lines

def _resolve_user_from_request():
    # header first, then ?user=..., then form
    u = request.headers.get("X-User") or request.args.get("user") or request.form.get("user")
    if not u: return None
    return safe_path_seg(u, default=None)

def _merge_scansat(save_safe: str):
    """Merge per-user SCANcontroller snapshots -> SCANcontroller.txt."""
    base = os.path.join(UPLOAD_FOLDER_SCENARIOS, save_safe)
    user_dir = os.path.join(base, "SCANcontroller_users")
    os.makedirs(user_dir, exist_ok=True)
    merged_path = os.path.join(base, "SCANcontroller.txt")

    scanners, bodies = {}, {}
    flags_any = {}

    res_block = None  # keep the newest SCANResources block if any

    for fpath in sorted(glob.glob(os.path.join(user_dir, "*.txt"))):
        try:
            with open(fpath, "r", encoding="utf-8", errors="ignore") as f:
                txt = f.read()
        except Exception:
            continue

        # keep latest vessel entries per guid
        for guid, blk in _blocks_by("Vessel", txt).items():
            scanners[guid] = blk

        # OR coverage maps per body
        for bname, blk in _blocks_by("Body", txt).items():
            if bname not in bodies:
                bodies[bname] = blk
            else:
                a = _extract_map(bodies[bname])
                b = _extract_map(blk)
                bodies[bname] = _replace_map(bodies[bname], _map_union(a, b))

        flags_any = _merge_header_flags(txt, flags_any)

        # prefer the last (newest) resources section we see
        rb = _single_block("SCANResources", txt)
        if rb:
            res_block = rb

    # Compose a canonical SCENARIO node (what KSP expects)
    out = []
    out.append("SCENARIO")
    out.append("\t{")
    out.append("\t\tname = SCANcontroller")
    out.append("\t\tscene = 7, 5, 8")
    out.extend(_render_header_flags(flags_any))

    out.append("\t\tScanners")
    out.append("\t\t{")
    for guid in sorted(scanners.keys()):
        for ln in scanners[guid].splitlines():
            out.append("\t\t\t" + ln.strip())
    out.append("\t\t}")

    out.append("\t\tProgress")
    out.append("\t\t{")
    for body in sorted(bodies.keys()):
        for ln in bodies[body].splitlines():
            out.append("\t\t\t" + ln.strip())
    out.append("\t\t}")

    if res_block:
        for ln in res_block.splitlines():
            out.append("\t\t" + ln.strip())

    out.append("\t}")
    with open(merged_path, "w", encoding="utf-8") as f:
        f.write("\n".join(out) + "\n")
    return merged_path

# Other Scenario syncs
UPLOAD_FOLDER_SCENARIOS = os.path.join(os.path.dirname(__file__), 'scenarios')
os.makedirs(UPLOAD_FOLDER_SCENARIOS, exist_ok=True)

def safe_path_seg(name, default="default"):
    return "".join(c for c in (name or default) if c.isalnum() or c in "_-.")

@app.route('/scenarios/<save>/<module>', methods=['POST'])
def upload_scenario(save, module):
    save_safe = safe_path_seg(save)
    module_safe = safe_path_seg(module)
    folder = os.path.join(UPLOAD_FOLDER_SCENARIOS, save_safe)
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, module_safe + ".txt")

    new_data = request.data.decode("utf-8").strip()

    if module_safe == "SciencePoints":
        try:
            new_value = float(new_data.strip().split('=')[-1])
        except:
            return "Invalid format", 400
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                try:
                    old_value = float(f.read().strip().split('=')[-1])
                    if new_value > old_value:
                        with open(path, "w", encoding="utf-8") as wf:
                            wf.write(f"sci = {new_value}\n")
                except:
                    with open(path, "w", encoding="utf-8") as wf:
                        wf.write(f"sci = {new_value}\n")
        else:
            with open(path, "w", encoding="utf-8") as wf:
                wf.write(f"sci = {new_value}\n")

    elif module_safe == "TechTree":
        def extract_tech_ids_and_costs(text):
            ids = set()
            blocks = []
            cost_map = {}
            current_id = None
            current_cost = 0
            for line in text.splitlines():
                line = line.strip()
                if line.startswith("Tech"):
                    current_id = None
                    current_cost = 0
                elif line.startswith("id = "):
                    current_id = line.split("=", 1)[1].strip()
                    ids.add(current_id)
                elif line.startswith("cost = ") and current_id:
                    try:
                        current_cost = float(line.split("=", 1)[1].strip())
                        cost_map[current_id] = current_cost
                    except:
                        pass
            return ids, cost_map

        def extract_tech_blocks(text):
            blocks = []
            block = []
            inside_block = False
            for line in text.splitlines():
                if line.strip().startswith("Tech"):
                    inside_block = True
                    block = [line]
                elif inside_block:
                    block.append(line)
                    if line.strip() == "}":
                        blocks.append("\n".join(block))
                        inside_block = False
            return blocks

        new_ids, new_costs = extract_tech_ids_and_costs(new_data)
        new_blocks = extract_tech_blocks(new_data)

        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                existing_data = f.read()
            existing_ids, _ = extract_tech_ids_and_costs(existing_data)
            existing_blocks = extract_tech_blocks(existing_data)

            # Merge unique blocks
            merged_blocks = existing_blocks[:]
            unlocked_cost = 0.0
            for block in new_blocks:
                block_id = None
                for line in block.splitlines():
                    if line.strip().startswith("id = "):
                        block_id = line.split("=", 1)[1].strip()
                        break
                if block_id and block_id not in existing_ids:
                    merged_blocks.append(block)
                    unlocked_cost += new_costs.get(block_id, 0.0)

            # Write updated TechTree
            with open(path, "w", encoding="utf-8") as f:
                f.write("\n".join(merged_blocks).strip() + "\n")

            # Adjust SciencePoints.txt
            points_path = os.path.join(folder, "SciencePoints.txt")
            if os.path.exists(points_path):
                try:
                    with open(points_path, "r", encoding="utf-8") as f:
                        current_points = float(f.read().strip().split('=')[-1])
                    new_points = max(0.0, current_points - unlocked_cost)
                    with open(points_path, "w", encoding="utf-8") as f:
                        f.write(f"sci = {new_points:.6f}\n")
                except:
                    pass

        else:
            # First time upload: accept as-is
            with open(path, "w", encoding="utf-8") as f:
                f.write(new_data.strip() + "\n")


    elif module_safe == "ScienceArchives":
        # --- Helpers ---------------------------------------------------------
        def extract_science_blocks(text: str):
            blocks, cur, inside = [], [], False
            for line in (text or "").strip().splitlines():
                s = line.strip()
                if s.startswith("Science"):
                    if inside and cur:
                        blocks.append("\n".join(cur))
                        cur = []
                    inside = True
                    cur = [line.rstrip()]
                elif inside:
                    cur.append(line.rstrip())
                    if s == "}":
                        blocks.append("\n".join(cur))
                        cur = []
                        inside = False
            if cur:
                blocks.append("\n".join(cur))
            return blocks

        def parse_block(block: str):
            sid, sci, cap = None, None, None
            for ln in block.splitlines():
                s = ln.strip()
                if s.startswith("id ="):
                    sid = s.split("=", 1)[1].strip()
                elif s.startswith("sci ="):
                    try: sci = float(s.split("=", 1)[1].strip())
                    except: pass
                elif s.startswith("cap ="):
                    try: cap = float(s.split("=", 1)[1].strip())
                    except: pass
            return sid, sci, cap

        def replace_sci(block: str, new_sci: float) -> str:
            # keep original indentation of the sci line
            out, replaced = [], False
            for ln in block.splitlines():
                if not replaced and ln.strip().startswith("sci ="):
                    indent = ln[:len(ln) - len(ln.lstrip())]
                    out.append(f"{indent}sci = {new_sci}")
                    replaced = True
                else:
                    out.append(ln)
            # (If a malformed block lacks sci, append one before closing brace)
            if not replaced:
                for i in range(len(out)-1, -1, -1):
                    if out[i].strip() == "}":
                        out.insert(i, f"    sci = {new_sci}")
                        break
            return "\n".join(out)

        # --- Load & merge ----------------------------------------------------
        new_blocks = extract_science_blocks(new_data)
        merged_map = {}  # id -> {'raw': block, 'sci': float or None, 'cap': float or None}

        # start from existing file to preserve previous subjects
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                existing_blocks = extract_science_blocks(f.read())
            for b in existing_blocks:
                sid, sci, cap = parse_block(b)
                if sid:
                    merged_map[sid] = {'raw': b, 'sci': sci, 'cap': cap}

        # merge in new blocks by id, clamping to cap
        for b in new_blocks:
            sid, sci, cap = parse_block(b)
            if not sid:
                continue
            prev = merged_map.get(sid, {'raw': b, 'sci': None, 'cap': None})

            # pick final cap (prefer non-None; cap is fixed per subject)
            cap_final = cap if cap is not None else prev.get('cap')

            # best known sci is the max of previous and new
            best_sci = prev['sci'] if isinstance(prev['sci'], (int, float)) else 0.0
            if isinstance(sci, (int, float)):
                if sci > best_sci:
                    best_sci = sci

            # clamp to cap if available
            if isinstance(cap_final, (int, float)):
                best_sci = min(best_sci, cap_final)

            # prefer the newer block layout when updating
            base_block = b if isinstance(sci, (int, float)) and (prev['sci'] is None or sci >= prev['sci']) else prev['raw']
            out_block = replace_sci(base_block, best_sci)

            merged_map[sid] = {'raw': out_block, 'sci': best_sci, 'cap': cap_final}

        # write back unique, merged blocks
        with open(path, "w", encoding="utf-8") as f:
            # stable-ish order by id
            f.write("\n".join(merged_map[k]['raw'] for k in sorted(merged_map.keys())) + "\n")

    elif module_safe == "SCANcontroller":
        user = _resolve_user_from_request()
        if not user:
            return "Missing user (send X-User header or ?user=...)", 400

        base = os.path.join(UPLOAD_FOLDER_SCENARIOS, save_safe)
        user_dir = os.path.join(base, "SCANcontroller_users")
        os.makedirs(user_dir, exist_ok=True)
        user_path = os.path.join(user_dir, user + ".txt")

        with open(user_path, "w", encoding="utf-8") as f:
            f.write(new_data + "\n")

        # update merged file on each upload
        _merge_scansat(save_safe)

                
    return "OK", 200



@app.route('/scenarios/<save>/<module>', methods=['GET'])
def download_scenario(save, module):
    save_safe = safe_path_seg(save)
    module_safe = safe_path_seg(module)
    folder = os.path.join(UPLOAD_FOLDER_SCENARIOS, save_safe)
    path = os.path.join(folder, module_safe + ".txt")

    if module_safe == "SCANcontroller":
        merged_path = _merge_scansat(save_safe)  # ensure up-to-date
        with open(merged_path, "r", encoding="utf-8") as f:
            return f.read(), 200, {'Content-Type': 'text/plain; charset=utf-8'}


    if module_safe == "SciencePoints":
        archives_path = os.path.join(folder, "ScienceArchives.txt")
        tech_path = os.path.join(folder, "TechTree.txt")
        total_science = 0.0
        total_cost = 0.0

        if os.path.exists(archives_path):
            with open(archives_path, "r", encoding="utf-8") as f:
                for line in f:
                    if line.strip().startswith("sci ="):
                        try:
                            total_science += float(line.strip().split("=")[1])
                        except:
                            pass

        if os.path.exists(tech_path):
            with open(tech_path, "r", encoding="utf-8") as f:
                for line in f:
                    if line.strip().startswith("cost ="):
                        try:
                            total_cost += float(line.strip().split("=")[1])
                        except:
                            pass

        final_points = max(0.0, total_science - total_cost)
        return f"sci = {final_points:.6f}\n", 200, {'Content-Type': 'text/plain; charset=utf-8'}

    elif os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            return f.read(), 200, {'Content-Type': 'text/plain; charset=utf-8'}
    else:
        return "Scenario not found", 404


# Optional: list scenario module files for a given save
@app.route('/scenarios/<save>/', methods=['GET'])
def list_scenario_files(save):
    save_safe = safe_path_seg(save)
    folder = os.path.join(UPLOAD_FOLDER_SCENARIOS, save_safe)
    if not os.path.exists(folder):
        return jsonify([])

    files = [f for f in os.listdir(folder) if f.endswith(".txt")]
    return jsonify(files)

# -----------------------
# Simple majority voting
# -----------------------
from collections import defaultdict
import time

VOTES = {}  # key: (save, techID) -> dict
# structure: {'title': str, 'requester': str, 'cost': float,
#             'votes': {user: True/False}, 'opened': ts, 'closed': False, 'approved': None}

def _key(save, tech): return (safe_path_seg(save), safe_path_seg(tech))

# at top of voting section keep existing imports and globals

def _online_users():
    now = time.time()
    out = []
    for user, rec in PRESENCE.items():
        try:
            if (now - float(rec.get("ut_epoch", 0))) < PRESENCE_TTL:
                out.append(user)
        except:
            pass
    return out

@app.route('/vote/start/<save>/<tech>', methods=['POST'])
def vote_start(save, tech):
    data = request.get_json(force=True, silent=True) or {}
    requester = data.get('user','Player')
    title = data.get('title', tech)
    cost  = float(data.get('cost', 0.0))
    k = _key(save, tech)

    # quorum: 1 if only one online, else 2
    online_cnt = max(0, len(_online_users()))
    quorum = 1 if online_cnt <= 1 else 2

    VOTES[k] = {'title': title, 'requester': requester, 'cost': cost,
                'votes': {}, 'opened': time.time(), 'closed': False,
                'approved': None, 'quorum': quorum}
    return 'OK', 200

@app.route('/vote/cast/<save>/<tech>', methods=['POST'])
def vote_cast(save, tech):
    data = request.get_json(force=True, silent=True) or {}
    user = (data.get('user') or 'Player').strip()
    vraw = data.get('vote', False)
    vote = vraw if isinstance(vraw, bool) else str(vraw).strip().lower() in ('1','true','yes','y','t')

    k = _key(save, tech)
    if k not in VOTES or VOTES[k].get('closed'):
        return 'No open vote', 400

    VOTES[k]['votes'][user] = vote

    yes = sum(1 for v in VOTES[k]['votes'].values() if v)
    no  = sum(1 for v in VOTES[k]['votes'].values() if not v)
    n   = yes + no
    quorum = int(VOTES[k].get('quorum', 2))

    if n >= quorum:
        VOTES[k]['closed'] = True
        VOTES[k]['approved'] = (yes > no)

    return 'OK', 200


@app.route('/vote/status/<save>/<tech>', methods=['GET'])
def vote_status(save, tech):
    k = _key(save, tech)
    v = VOTES.get(k)
    if not v:
        return jsonify({'decided': False}), 200

    yes = sum(1 for x in v['votes'].values() if x)
    no  = sum(1 for x in v['votes'].values() if not x)

    return jsonify({
        'title': v['title'],
        'requester': v['requester'],
        'yes': yes,
        'no': no,
        'decided': v['closed'],
        'approved': (True if v['approved'] is True else False) if v['approved'] is not None else None
    }), 200

@app.route('/vote/open/<save>', methods=['GET'])
def vote_open(save):
    s = safe_path_seg(save)
    lines = []
    for (sv, tech), v in list(VOTES.items()):
        if sv != s or v.get('closed'): continue
        lines.append(f"{tech}|{v['title']}|{v['requester']}")
    return "\n".join(lines), 200, {'Content-Type': 'text/plain; charset=utf-8'}

@app.route('/vote/cancel/<save>/<tech>', methods=['POST'])
def vote_cancel(save, tech):
    data = request.get_json(force=True, silent=True) or {}
    user = (data.get('user') or 'Player').strip()
    k = _key(save, tech)
    v = VOTES.get(k)
    if not v:
        return 'No vote', 200
    # only requester can cancel
    if user == v.get('requester'):
        v['closed'] = True
        v['approved'] = False
    return 'OK', 200

# --- Orbits (per-user files + merged GET) ---
ORBIT_FOLDER = os.path.join(os.path.dirname(__file__), 'orbits')
os.makedirs(ORBIT_FOLDER, exist_ok=True)

def _safe_seg(s: str) -> str:
    s = (s or "").strip()
    return "".join(c for c in s if c.isalnum() or c in "_-.")

def _orbit_dir(save_id: str) -> str:
    return os.path.join(ORBIT_FOLDER, _safe_seg(save_id))

def _user_path(save_id: str, user: str) -> str:
    return os.path.join(_orbit_dir(save_id), _safe_seg(user) + ".txt")

@app.route('/orbits/<save_id>', methods=['POST'])
def post_orbit(save_id):
    """
    Body: single CSV line
    user,vessel,body,epochUT,sma,ecc,inc_deg,lan_deg,argp_deg,mna_rad,colorHex,updatedUT
    Stores newest snapshot per user in its own file.
    """
    raw = (request.get_data(as_text=True) or "").strip()
    if not raw or ',' not in raw:
        return ("bad request", 400)

    parts = raw.split(',')
    if len(parts) < 12:
        return ("bad csv", 400)

    user = parts[0].strip()
    try:
        updated = float(parts[11])
    except Exception:
        updated = 0.0

    d = _orbit_dir(save_id)
    os.makedirs(d, exist_ok=True)
    upath = _user_path(save_id, user)

    # keep only newest for this user
    prev_updated = -1.0
    if os.path.exists(upath):
        try:
            with open(upath, 'r', encoding='utf-8') as f:
                prev = f.read().strip()
            if prev and ',' in prev:
                p2 = prev.split(',')
                if len(p2) >= 12:
                    prev_updated = float(p2[11])
        except Exception:
            prev_updated = -1.0

    if updated >= prev_updated:
        with open(upath, 'w', encoding='utf-8') as f:
            f.write(raw.strip() + "\n")

    return jsonify(ok=True)

@app.route('/orbits/<save_id>.txt', methods=['GET'])
def get_orbits(save_id):
    """
    Merges all per-user snapshots for this save into one text response.
    """
    d = _orbit_dir(save_id)
    if not os.path.isdir(d):
        return ("# empty\n", 200, {'Content-Type': 'text/plain; charset=utf-8',
                                   'Cache-Control': 'no-store'})

    merged = {}
    for fn in os.listdir(d):
        if not fn.lower().endswith(".txt"):
            continue
        path = os.path.join(d, fn)
        try:
            with open(path, 'r', encoding='utf-8') as f:
                line = f.readline().strip()
            if not line or ',' not in line:
                continue
            parts = line.split(',')
            if len(parts) < 12:
                continue
            user = parts[0].strip()
            updated = float(parts[11])
            prev = merged.get(user)
            if prev is None or updated >= prev[0]:
                merged[user] = (updated, line)
        except Exception:
            continue

    out_lines = ["# user,vessel,body,epochUT,sma,ecc,inc_deg,lan_deg,argp_deg,mna_rad,colorHex,updatedUT"]
    for _, line in sorted(merged.values(), key=lambda t: t[0], reverse=True):
        out_lines.append(line)

    body = "\n".join(out_lines) + "\n"
    return (body, 200, {'Content-Type': 'text/plain; charset=utf-8',
                        'Cache-Control': 'no-store'})

# -----------------------
# Presence: memory-first with per-user .txt cache
# -----------------------
import os, time, threading
from urllib.parse import unquote_plus
from flask import request, jsonify

PRESENCE_DIR = os.path.join(os.path.dirname(__file__), 'presence')
os.makedirs(PRESENCE_DIR, exist_ok=True)

PRESENCE_LOCK = threading.RLock()
# { user: {"scene": "Flight", "ut_epoch": 1730332800.0, "color": "#ffaa00", "updated": 1730332801.0} }
PRESENCE = {}
PRESENCE_TTL = 30  # seconds considered "online"

def _presence_path(user: str) -> str:
    # sanitize filename
    safe = "".join(c for c in (user or "") if c.isalnum() or c in "_-.")
    return os.path.join(PRESENCE_DIR, f"{safe}.txt")

def _presence_load_from_disk():
    with PRESENCE_LOCK:
        for fn in os.listdir(PRESENCE_DIR):
            if not fn.endswith(".txt"): 
                continue
            path = os.path.join(PRESENCE_DIR, fn)
            try:
                d = {}
                with open(path, "r", encoding="utf-8") as f:
                    for ln in f:
                        ln = ln.strip()
                        if "=" in ln:
                            k, v = ln.split("=", 1)
                            d[k.strip()] = v.strip()
                user = d.get("user")
                if not user:
                    continue
                ut_epoch = float(d.get("ut", d.get("ut_epoch", "0")) or 0)
                ksp_ut = float(d.get("ksp_ut", "0") or 0)          # <-- add
                PRESENCE[user] = {
                    "scene": d.get("scene", "Unknown"),
                    "ut_epoch": ut_epoch,
                    "ksp_ut": ksp_ut,                               # <-- add
                    "color": d.get("color", ""),
                    "updated": float(d.get("updated", ut_epoch)),
                }
            except:
                pass

def _presence_dump_to_disk(user: str, rec: dict):
    try:
        with open(_presence_path(user), "w", encoding="utf-8") as f:
            f.write(f"user={user}\n")
            f.write(f"scene={rec.get('scene','Unknown')}\n")
            f.write(f"ut={rec.get('ut_epoch', 0)}\n")   # epoch
            f.write(f"ksp_ut={rec.get('ksp_ut', 0)}\n")  # <-- add
            f.write(f"color={rec.get('color','')}\n")
            f.write(f"updated={rec.get('updated', 0)}\n")
    except:
        pass

def _presence_list():
    now = time.time()
    with PRESENCE_LOCK:
        out = []
        for user, rec in PRESENCE.items():
            rec2 = dict(rec)
            rec2["user"] = user
            rec2["online"] = (now - float(rec.get("ut_epoch", 0))) < PRESENCE_TTL
            out.append(rec2)
        out.sort(key=lambda r: r.get("ut_epoch", 0), reverse=True)
        return out

@app.route('/presence', methods=['GET'])
def presence_list():
    if not PRESENCE:
        _presence_load_from_disk()
    items = _presence_list()
    fmt = (request.args.get("format") or "").lower()
    if fmt == "json":
        return jsonify(items)
    lines = []
    for r in items:
        online = "1" if r.get("online") else "0"
        lines.append(
            "user={u},scene={s},ut={ue},ksp_ut={ku},color={c},online={o}".format(
                u=r.get('user'),
                s=r.get('scene'),
                ue=r.get('ut_epoch'),
                ku=r.get('ksp_ut', 0),      # <-- add
                c=r.get('color',''),
                o=online))
    body = "\n".join(lines) + ("\n" if lines else "")
    return body, 200, {'Content-Type': 'text/plain; charset=utf-8', 'Cache-Control': 'no-store'}

@app.route('/presence/<user>', methods=['GET'])
def presence_get(user):
    user = unquote_plus(user)
    with PRESENCE_LOCK:
        rec = PRESENCE.get(user)
    if not rec and os.path.exists(_presence_path(user)):
        _presence_load_from_disk()
        with PRESENCE_LOCK:
            rec = PRESENCE.get(user)
    if not rec:
        return ("not found", 404)
    now = time.time()
    online = (now - float(rec.get("ut_epoch", 0))) < PRESENCE_TTL
    return jsonify(dict(rec, user=user, online=online))

@app.route('/presence/<user>', methods=['POST'])
def presence_post(user):
    user = unquote_plus(user)
    data = request.get_json(silent=True) or {}
    scene = (data.get("scene") or request.form.get("scene") or "Unknown").strip()
    color = (data.get("color") or request.form.get("color") or "").strip()

    ut_epoch = data.get("ut") or data.get("ut_epoch") or request.form.get("ut") or request.form.get("ut_epoch")
    try: ut_epoch = float(ut_epoch)
    except: ut_epoch = time.time()

    ksp_ut = data.get("ksp_ut") or request.form.get("ksp_ut")
    try: ksp_ut = float(ksp_ut) if ksp_ut is not None else 0.0
    except: ksp_ut = 0.0

    rec = {"scene": scene, "ut_epoch": float(ut_epoch), "color": color,
           "updated": time.time(), "ksp_ut": ksp_ut}
    with PRESENCE_LOCK:
        PRESENCE[user] = rec
    _presence_dump_to_disk(user, rec)
    return ("OK", 200)

@app.route('/presence/<user>', methods=['DELETE'])
def presence_delete(user):
    user = unquote_plus(user)
    with PRESENCE_LOCK:
        PRESENCE.pop(user, None)
    try:
        os.remove(_presence_path(user))
    except:
        pass
    return ("OK", 200)


# --- Chat storage ------------------------------------------------------------
import base64
from datetime import datetime, timezone
from flask import Response

CHAT_FOLDER = os.path.join(os.path.dirname(__file__), 'chat')
os.makedirs(CHAT_FOLDER, exist_ok=True)

def _chat_path(save):
    return os.path.join(CHAT_FOLDER, safe_path_seg(save) + ".txt")


def _b64(s: str) -> str:
    return base64.b64encode(s.encode('utf-8')).decode('ascii')

@app.route('/chat/<save>', methods=['GET'])
def chat_get(save):
    path = _chat_path(save)
    if not os.path.exists(path):
        return ("", 200, {'Content-Type': 'text/plain; charset=utf-8',
                          'Cache-Control': 'no-store'})
    with open(path, 'r', encoding='utf-8') as f:
        body = f.read()
    # Optional: trim file if it grows too large (manual maintenance)
    return (body, 200, {'Content-Type': 'text/plain; charset=utf-8',
                        'Cache-Control': 'no-store'})

@app.route('/chat/<save>', methods=['POST'])
def chat_post(save):
    user = request.args.get('u', 'anon')
    msg  = (request.data or b'').decode('utf-8', errors='ignore').strip()
    if not msg:
        return ("empty", 400)
    if len(msg) > 300:
        msg = msg[:300]

    ts = datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')  # real time UTC
    line = f"{ts}|{_b64(user)}|{_b64(msg)}\n"

    path = _chat_path(save)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'a', encoding='utf-8') as f:
        f.write(line)

    return ("OK", 200, {'Cache-Control': 'no-store'})


# --- science: subjects + archives ---
import os, json
from flask import jsonify, send_from_directory

# Serve the static page
@app.route('/science.html')
def serve_science_html():
    return send_from_directory(os.path.dirname(__file__), 'science.html')

# Prebuilt subjects JSON (fast; no CFG parsing at runtime)
@app.route('/science/subjects')
def science_subjects_cached():
    root = os.path.dirname(__file__)
    with open(os.path.join(root, 'science_subjects.json'), 'r', encoding='utf-8') as f:
        return jsonify(json.load(f))

# DONE list from a save's archives (uses your existing folder layout)
@app.route('/science/archives/<save>')
def science_archives_json(save):
    # if you already have safe_path_seg/UPLOAD_FOLDER_SCENARIOS, use those:
    try:
        save_safe = safe_path_seg(save)
        base = UPLOAD_FOLDER_SCENARIOS
    except NameError:
        # fallback if not defined
        save_safe = save.replace('/', '_').replace('..', '')
        base = os.path.join(os.path.dirname(__file__), 'scenarios')

    path = os.path.join(base, save_safe, 'ScienceArchives.txt')
    ids = set()
    if os.path.exists(path):
        inside = False; cur_id = None
        with open(path, 'r', encoding='utf-8', errors='ignore') as f:
            for raw in f:
                s = raw.strip()
                if s.startswith('Science'):
                    inside = True; cur_id = None
                elif inside and s.startswith('id ='):
                    cur_id = s.split('=',1)[1].strip()
                elif inside and s == '}':
                    if cur_id: ids.add(cur_id)
                    inside = False
    return jsonify(sorted(ids))

if __name__ == '__main__':
    print(f"Serving vessels from: {UPLOAD_FOLDER_VESSELS}")
    print(f"Serving flags from: {UPLOAD_FOLDER_FLAGS}")
    print(f"Serving presence from: {PRESENCE_DIR}")
    app.run(host="0.0.0.0", port=5000, debug=False)