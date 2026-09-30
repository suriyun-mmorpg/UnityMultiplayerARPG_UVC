#if UNITY_EDITOR
using UnityEngine;
namespace MultiplayerARPG
{
    // Editor-only fixture: exercise the real ApplyDamage dispatch without live character services/RPCs.
    public class UVCHitCharacterValidationProbe : PlayerCharacterEntity
    {
        public MapInfo TestMap;
        public bool BlockDamage;
        public int Hits;
        public EntityInfo LastDriver;
        public float RawDamage;
        private bool _invincible;
        public override bool IsInvincible { get => _invincible || IsInSafeArea; set => _invincible = value; }
        public override EntityInfo GetInfo() => new EntityInfo().SetEntityInfo(EntityTypes.Player, ObjectId,
            name, SubChannelId, 0, FactionId, PartyId, GuildId, IsInSafeArea, this, null);
        public override bool CanReceiveDamageFrom(EntityInfo driver) => !BlockDamage && base.CanReceiveDamageFrom(driver) &&
            (TestMap == null || !TestMap.IsAlly(this, driver));
        protected override DamageElementMinMaxFloatAmounts PrepareDamageAmountsForReceive(HitBoxPosition position,
            DamageElementMinMaxFloatAmounts amounts) => amounts;
        protected override void ApplyReceiveDamage(HitBoxPosition position, Vector3 from, EntityInfo driver,
            DamageElementMinMaxFloatAmounts amounts, CharacterItem weapon, BaseSkill skill, int level, int seed,
            out CombatAmountType type, out int damage)
        {
            LastDriver = driver;
            RawDamage = amounts[0].min;
            damage = Mathf.CeilToInt(RawDamage * 0.5f); // Simulated mitigation proves the combat pipeline owns final HP damage.
            CurrentHp -= damage;
            type = CombatAmountType.NormalDamage;
        }
        public override void ReceivedDamage(HitBoxPosition position, Vector3 from, EntityInfo driver,
            DamageElementMinMaxFloatAmounts amounts, CombatAmountType type, int damage, CharacterItem weapon,
            BaseSkill skill, int level, CharacterBuff buff, bool dot = false) { ++Hits; }
    }
}
#endif
