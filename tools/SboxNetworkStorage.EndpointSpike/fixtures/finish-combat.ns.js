endpoint({
  id: "finish-combat", method: "POST",
  input: { type: "object", properties: {
    hits: { type: "number" }, client_time_ms: { type: "number" }
  }, required: ["hits", "client_time_ms"] }
}, ({ input, steamId, read, values, now, exists, floor, abs, reject, write }) => {
  const player = read("players", steamId);
  if (!exists(player.combat.started_ms))
    reject(409, "NO_COMBAT", "Start combat first.");
  const weapon = values.find("weapons", "weapon_id", player.combat.weapon_id,
    { status: 404, code: "UNKNOWN_WEAPON" });
  const elapsed_ms = now - player.combat.started_ms;
  const damage = input.hits * weapon.damage;
  const max_hits = floor(elapsed_ms / weapon.fire_interval_ms) + 1;
  const clock_skew_ms = abs(input.client_time_ms - now);
  const xp = player.combat.zombie_level * values.combat.xp_per_zombie_level;
  if (damage < player.combat.zombie_hp)
    reject(400, "ZOMBIE_ALIVE", "Not enough damage to kill this zombie.");
  if (input.hits > max_hits)
    reject(400, "TOO_MANY_HITS", "More hits than this weapon can fire in the time since combat started.");
  if (clock_skew_ms > values.combat.max_clock_skew_ms)
    reject(400, "CLOCK_SKEW", "Client clock is too far from the server clock.");
  write("players", steamId, {
    inc: { xp }, set: { combat: { ended_ms: now } }
  });
  return { status: 200, body: { success: true, xp, elapsed_ms } };
});
