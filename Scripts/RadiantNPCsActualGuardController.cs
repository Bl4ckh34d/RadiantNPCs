using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;

namespace RadiantNPCsMod
{
    public class RadiantNPCsActualGuardController : MonoBehaviour
    {
        private const float CalmCheckInterval = 0.75f;

        private RadiantNPCsMain main;
        private int mapId = -1;
        private int residentId = -1;
        private EnemyMotor enemyMotor;
        private EnemySenses enemySenses;
        private DaggerfallEntityBehaviour entityBehaviour;
        private DaggerfallEntity subscribedEntity;
        private float nextCalmCheckAt = 0f;

        public void Configure(RadiantNPCsMain main, int mapId, int residentId)
        {
            this.main = main;
            this.mapId = mapId;
            this.residentId = residentId;
            enemyMotor = GetComponent<EnemyMotor>();
            enemySenses = GetComponent<EnemySenses>();
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            SubscribeDeath();
            nextCalmCheckAt = 0f;
        }

        private void OnDestroy()
        {
            if (subscribedEntity != null)
                subscribedEntity.OnDeath -= Entity_OnDeath;
        }

        private void Update()
        {
            if (main == null || GameManager.IsGamePaused)
                return;
            if (Time.time < nextCalmCheckAt)
                return;

            nextCalmCheckAt = Time.time + CalmCheckInterval;
            if (!IsCalm())
                return;

            main.TryRestorePromotedGuardResident(mapId, residentId, transform.position, transform.forward, gameObject);
        }

        private void SubscribeDeath()
        {
            if (entityBehaviour == null || entityBehaviour.Entity == null || entityBehaviour.Entity == subscribedEntity)
                return;

            if (subscribedEntity != null)
                subscribedEntity.OnDeath -= Entity_OnDeath;

            subscribedEntity = entityBehaviour.Entity;
            subscribedEntity.OnDeath += Entity_OnDeath;
        }

        private void Entity_OnDeath(DaggerfallEntity entity)
        {
            if (main != null)
                main.NotifyResidentDeath(mapId, residentId);
        }

        private bool IsCalm()
        {
            if (entityBehaviour == null || entityBehaviour.Entity == null || entityBehaviour.Entity.CurrentHealth <= 0)
                return false;
            if (enemyMotor == null || enemySenses == null)
                return false;
            if (enemyMotor.IsHostile)
                return false;
            if (enemySenses.Target != null || enemySenses.SecondaryTarget != null)
                return false;
            if (enemyMotor.GiveUpTimer > 0)
                return false;
            if (enemySenses.LastKnownTargetPos != EnemySenses.ResetPlayerPos)
                return false;

            return true;
        }
    }
}
