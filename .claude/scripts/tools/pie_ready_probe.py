"""ORCH: read-only. Prints what wait_result.py --pie-ready and --pie-stopped decide on, as one line:

    PIE_READY_PROBE {"game_world": ..., "authority": ..., "dedicated": ..., "clients": ..., "players": ..., "player_states": ...}

Run it through ue_python.py. It only reads the PIE worlds: game_world is whether any is up, authority whether one has a
game mode, dedicated whether that one is a dedicated server's, clients how many other worlds there are, and players and
player_states what the authority world holds.
"""
import json

import unreal

every_world = list(unreal.EditorLevelLibrary.get_pie_worlds(True) or [])
visible_worlds = list(unreal.EditorLevelLibrary.get_pie_worlds(False) or [])
authority = next((world for world in every_world if unreal.GameplayStatics.get_game_mode(world)), None)
facts = {
    "game_world": bool(every_world),
    "authority": authority is not None,
    "dedicated": authority is not None and authority not in visible_worlds,
    "clients": sum(1 for world in every_world if world != authority),
    "players": 0,
    "player_states": 0,
}
if authority is not None:
    facts["players"] = len(unreal.GameplayStatics.get_all_actors_of_class(authority, unreal.PlayerController))
    facts["player_states"] = len(unreal.GameplayStatics.get_all_actors_of_class(authority, unreal.PlayerState))
print("PIE_READY_PROBE " + json.dumps(facts))
