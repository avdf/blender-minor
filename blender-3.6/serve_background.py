# Sobe o servidor do add-on MCP dentro do Blender 3.6, SEM janela (equivalente ao 'blender --command blender_mcp' do 4.2+).
# Uso: BLENDER_USER_SCRIPTS=<pasta>/blender-scripts blender -b --factory-startup --python serve_background.py -- --port 9877
import sys
import addon_utils

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
addon_utils.enable("blender_mcp_addon", default_set=False, persistent=False)
if "blender_mcp_addon" not in sys.modules:
    print("ERRO: add-on nao carregou"); sys.exit(2)
from blender_mcp_addon.cli import cli_execute
sys.exit(cli_execute(argv))
