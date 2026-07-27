using UnityEngine;

namespace ArenaFps.Core
{
    /// <summary>
    /// Marks an actor as belonging to a TDM team. Friendly fire checks and bot targeting read this.
    /// </summary>
    public sealed class TeamMember : MonoBehaviour
    {
        [SerializeField] TeamId team = TeamId.None;

        public TeamId Team
        {
            get => team;
            set => team = value;
        }

        public bool IsEnemyOf(TeamMember other)
        {
            if (other == null || team == TeamId.None || other.team == TeamId.None)
                return true;
            return team != other.team;
        }

        public bool IsEnemyOf(TeamId other)
        {
            if (team == TeamId.None || other == TeamId.None)
                return true;
            return team != other;
        }

        public static Color Tint(TeamId id) => id switch
        {
            TeamId.Blue => new Color(0.25f, 0.55f, 1f),
            TeamId.Red => new Color(1f, 0.28f, 0.22f),
            _ => Color.white,
        };
    }
}
