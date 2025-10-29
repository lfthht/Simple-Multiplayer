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
        def extract_science_blocks(text):
            blocks = []
            current_block = []
            inside_science = False
            for line in text.strip().splitlines():
                stripped = line.strip()
                if stripped.startswith("Science"):
                    if inside_science and current_block:
                        blocks.append("\n".join(current_block))
                        current_block = []
                    inside_science = True
                    current_block = [stripped]
                elif inside_science:
                    current_block.append(stripped)
                    if stripped == "}":
                        blocks.append("\n".join(current_block))
                        current_block = []
                        inside_science = False
            if current_block:
                blocks.append("\n".join(current_block))
            return blocks

        new_blocks = extract_science_blocks(new_data)
        new_ids = {block for block in new_blocks if "id =" in block}

        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                existing_data = f.read()
            existing_blocks = extract_science_blocks(existing_data)

            # avoid duplicates by checking for same "id = ..."
            existing_ids = {block for block in existing_blocks if "id =" in block}

            merged_blocks = list(existing_blocks)
            for block in new_blocks:
                if block not in existing_ids:
                    merged_blocks.append(block)

            with open(path, "w", encoding="utf-8") as f:
                f.write("\n".join(merged_blocks) + "\n")
        else:
            with open(path, "w", encoding="utf-8") as f:
                f.write("\n".join(new_blocks) + "\n")
                
    return "OK", 200



@app.route('/scenarios/<save>/<module>', methods=['GET'])
def download_scenario(save, module):
    save_safe = safe_path_seg(save)
    module_safe = safe_path_seg(module)
    folder = os.path.join(UPLOAD_FOLDER_SCENARIOS, save_safe)
    path = os.path.join(folder, module_safe + ".txt")

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

@app.route('/vote/start/<save>/<tech>', methods=['POST'])
def vote_start(save, tech):
    data = request.get_json(force=True, silent=True) or {}
    requester = data.get('user','Player')
    title = data.get('title', tech)
    cost  = float(data.get('cost', 0.0))
    k = _key(save, tech)
    VOTES[k] = {'title': title, 'requester': requester, 'cost': cost,
                'votes': {}, 'opened': time.time(), 'closed': False, 'approved': None}
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

    # record or overwrite this user's vote
    VOTES[k]['votes'][user] = vote

    yes = sum(1 for v in VOTES[k]['votes'].values() if v)
    no  = sum(1 for v in VOTES[k]['votes'].values() if not v)
    n   = yes + no

    # decide as soon as two distinct users have voted:
    # approve if yes>no, otherwise reject on tie or more no
    if n >= 2:
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

# --- Orbits (minimal CSV aggregator) ---
ORBIT_FOLDER = os.path.join(os.path.dirname(__file__), 'orbits')
os.makedirs(ORBIT_FOLDER, exist_ok=True)

def _orbit_path(save_id: str) -> str:
    safe = save_id.replace('/', '_').replace('\\', '_')
    return os.path.join(ORBIT_FOLDER, f"{safe}.txt")

@app.route('/orbits/<save_id>.txt', methods=['GET'])
def get_orbits(save_id):
    path = _orbit_path(save_id)
    if not os.path.exists(path):
        return ("# empty\n", 200, {'Content-Type': 'text/plain; charset=utf-8'})
    return send_from_directory(ORBIT_FOLDER, os.path.basename(path), mimetype='text/plain')

@app.route('/orbits/<save_id>', methods=['POST'])
def post_orbit(save_id):
    """
    Body: single CSV line
    user,vessel,body,epochUT,sma,ecc,inc_deg,lan_deg,argp_deg,mna_rad,colorHex,updatedUT
    Server keeps newest entry per user.
    """
    raw = request.get_data(as_text=True).strip()
    if not raw: return ("bad request", 400)

    path = _orbit_path(save_id)
    existing = {}
    if os.path.exists(path):
        with open(path, 'r', encoding='utf-8') as f:
            for ln in f:
                if not ln or ln.startswith('#') or ',' not in ln: continue
                parts = ln.strip().split(',')
                if len(parts) < 12: continue
                user = parts[0].strip()
                try:
                    updated = float(parts[11])
                except:
                    updated = 0.0
                existing[user] = (updated, ln.strip())

    parts = raw.split(',')
    if len(parts) < 12: return ("bad csv", 400)
    user = parts[0].strip()
    try:
        updated = float(parts[11])
    except:
        updated = 0.0

    prev = existing.get(user)
    if prev is None or updated >= prev[0]:
        existing[user] = (updated, raw)

    with open(path, 'w', encoding='utf-8') as f:
        f.write("# user,vessel,body,epochUT,sma,ecc,inc_deg,lan_deg,argp_deg,mna_rad,colorHex,updatedUT\n")
        for _, line in sorted(existing.values(), key=lambda t: t[0], reverse=True):
            f.write(line.strip() + "\n")

    return jsonify(ok=True)


if __name__ == '__main__':
    print(f"Serving vessels from: {UPLOAD_FOLDER_VESSELS}")
    print(f"Serving flags from: {UPLOAD_FOLDER_FLAGS}")
    app.run(debug=True, port=5000)