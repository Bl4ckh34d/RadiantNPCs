using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;

namespace RadiantNPCsMod
{
    public class RadiantNPCsNpcDeathRelay : MonoBehaviour
    {
        private RadiantNPCsMain main;
        private DaggerfallEntityBehaviour entityBehaviour;
        private DaggerfallEntity subscribedEntity;

        public RadiantNPCsMain Main
        {
            get { return main; }
            set
            {
                main = value;
                EnsureSubscribed();
            }
        }

        private void Awake()
        {
            EnsureSubscribed();
        }

        private void OnDestroy()
        {
            UnsubscribeEntity();
            if (entityBehaviour != null)
                entityBehaviour.OnSetEntity -= EntityBehaviour_OnSetEntity;
        }

        private void EnsureSubscribed()
        {
            if (entityBehaviour == null)
            {
                entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
                if (entityBehaviour == null)
                    return;

                entityBehaviour.OnSetEntity += EntityBehaviour_OnSetEntity;
            }

            if (entityBehaviour.Entity != subscribedEntity)
            {
                UnsubscribeEntity();
                subscribedEntity = entityBehaviour.Entity;
                if (subscribedEntity != null)
                    subscribedEntity.OnDeath += Entity_OnDeath;
            }
        }

        private void EntityBehaviour_OnSetEntity(DaggerfallEntity oldEntity, DaggerfallEntity newEntity)
        {
            EnsureSubscribed();
        }

        private void Entity_OnDeath(DaggerfallEntity entity)
        {
            MobilePersonNPC npc = GetComponent<MobilePersonNPC>();
            if (main != null && npc != null)
                main.NotifyResidentDeath(npc);
        }

        private void UnsubscribeEntity()
        {
            if (subscribedEntity != null)
                subscribedEntity.OnDeath -= Entity_OnDeath;

            subscribedEntity = null;
        }
    }
}
