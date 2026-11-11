namespace Game.Domain;

// Valida y resuelve una ronda simultánea en el contexto del juego.
public sealed class RoundResolver(ICriticalHitSource criticalHitSource)
{
    public RoundResolution Resolve(
        MatchState match,
        RoundPlan firstPlan,
        RoundPlan secondPlan,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(firstPlan);
        ArgumentNullException.ThrowIfNull(secondPlan);

        if (criticalHitSource is null)
        {
            throw new ArgumentNullException(nameof(criticalHitSource));
        }

        if (match.Status != MatchStatus.Active)
        {
            throw new InvalidOperationException("The match is not active.");
        }

        ValidatePlan(firstPlan, match.FirstPlayer, match.RoundNumber);
        ValidatePlan(secondPlan, match.SecondPlayer, match.RoundNumber);

        if (firstPlan.PlayerId == secondPlan.PlayerId)
        {
            throw new InvalidOperationException("A player cannot submit both round plans.");
        }

        int firstAttacks = Count(firstPlan, GameAction.Attack);
        int secondAttacks = Count(secondPlan, GameAction.Attack);
        int firstSpecialAttacks = Count(firstPlan, GameAction.SpecialAttack);
        int secondSpecialAttacks = Count(secondPlan, GameAction.SpecialAttack);
        int firstBlocks = Count(firstPlan, GameAction.Block);
        int secondBlocks = Count(secondPlan, GameAction.Block);
        int firstReserved = Count(firstPlan, GameAction.Reserve);
        int secondReserved = Count(secondPlan, GameAction.Reserve);

        int firstSpecialBlocked = Math.Min(firstSpecialAttacks, secondBlocks);
        int secondSpecialBlocked = Math.Min(secondSpecialAttacks, firstBlocks);
        int firstNormalBlocked = Math.Min(firstAttacks, secondBlocks - firstSpecialBlocked);
        int secondNormalBlocked = Math.Min(secondAttacks, firstBlocks - secondSpecialBlocked);
        int firstBlocked = firstSpecialBlocked + firstNormalBlocked;
        int secondBlocked = secondSpecialBlocked + secondNormalBlocked;

        int firstDamage = RollDamage(firstAttacks - firstNormalBlocked, out int firstCriticals)
            + (firstSpecialAttacks - firstSpecialBlocked) * MatchRules.SpecialAttackDamage;
        int secondDamage = RollDamage(secondAttacks - secondNormalBlocked, out int secondCriticals)
            + (secondSpecialAttacks - secondSpecialBlocked) * MatchRules.SpecialAttackDamage;

        int firstHealth = Math.Max(0, match.FirstPlayer.Health - secondDamage);
        int secondHealth = Math.Max(0, match.SecondPlayer.Health - firstDamage);
        // Valor que indica si la partida ha terminado por KO
        bool playerKnockedOut = firstHealth == 0 || secondHealth == 0;
        // Valor que indica si la partida ha terminado por límite de rondas (10)
        bool roundLimitReached = match.RoundNumber >= MatchRules.MaximumRounds;
        bool isFinished = playerKnockedOut || roundLimitReached;

        PlayerState firstPlayer = AdvancePlayer(
            match.FirstPlayer, firstHealth, firstReserved, firstBlocks, firstSpecialAttacks > 0, isFinished);
        PlayerState secondPlayer = AdvancePlayer(
            match.SecondPlayer, secondHealth, secondReserved, secondBlocks, secondSpecialAttacks > 0, isFinished);

        Guid? winnerId = null;
        if (firstHealth == 0 && secondHealth > 0)
        {
            winnerId = match.SecondPlayer.PlayerId;
        }
        else if (secondHealth == 0 && firstHealth > 0)
        {
            winnerId = match.FirstPlayer.PlayerId;
        }
        else if (!playerKnockedOut && roundLimitReached)
        {
            winnerId = firstHealth > secondHealth
                ? match.FirstPlayer.PlayerId
                : secondHealth > firstHealth
                    ? match.SecondPlayer.PlayerId
                    : null;
        }

        MatchState updatedMatch = match with
        {
            RoundNumber = isFinished ? match.RoundNumber : match.RoundNumber + 1,
            FirstPlayer = firstPlayer,
            SecondPlayer = secondPlayer,
            Status = isFinished ? MatchStatus.Finished : MatchStatus.Active,
            WinnerId = winnerId,
            SequenceNumber = match.SequenceNumber + 1,
            LastActivityAt = now
        };

        return new RoundResolution(
            updatedMatch,
            firstDamage,
            secondDamage,
            firstCriticals,
            secondCriticals,
            firstBlocked,
            secondBlocked);
    }
    
    // Valida que el plan de ronda sea válido para el jugador correspondiente.
    public static void ValidatePlan(RoundPlan plan, PlayerState player, int roundNumber)
    {
        if (plan.PlayerId != player.PlayerId)
        {
            throw new InvalidOperationException("The round plan does not belong to the expected player.");
        }

        if (plan.Actions is null)
        {
            throw new InvalidOperationException("A round plan must include its actions.");
        }

        if (plan.Actions.Any(action => !Enum.IsDefined(action)))
        {
            throw new InvalidOperationException("The round plan contains an unknown action.");
        }

        int energyCost = plan.Actions.Sum(MatchRules.EnergyCostFor);
        if (energyCost != player.Energy)
        {
            throw new InvalidOperationException("A player must spend all available energy this round.");
        }

        int blockadesInPlan = Count(plan, GameAction.Block);
        if (player.UsedBlockades + blockadesInPlan > MatchRules.MaximumBlockadesPerMatch)
        {
            throw new InvalidOperationException("A player cannot use more than five blockades in a match.");
        }

        int specialAttacksInPlan = Count(plan, GameAction.SpecialAttack);
        if (specialAttacksInPlan > 0 && roundNumber < MatchRules.SpecialAttackUnlockRound)
        {
            throw new InvalidOperationException("The special attack is available starting in round five.");
        }

        if (specialAttacksInPlan > 0 && (specialAttacksInPlan > 1 || player.HasUsedSpecialAttack))
        {
            throw new InvalidOperationException("A player can use the special attack only once per match.");
        }
    }

    // Calcular el daño total de los ataques sobrevivientes y contar los golpes críticos.
    private int RollDamage(int survivingAttacks, out int criticalHits)
    {
        int damage = 0;
        criticalHits = 0;

        for (int attack = 0; attack < survivingAttacks; attack++)
        {
            int roll = criticalHitSource.NextBasisPointRoll();
            if (roll is < 0 or >= 10_000)
            {
                throw new InvalidOperationException("The critical-hit source returned an invalid roll.");
            }

            bool isCritical = roll < MatchRules.CriticalChanceBasisPoints;
            if (isCritical)
            {
                criticalHits++;
            }

            damage += MatchRules.AttackDamage * (isCritical ? MatchRules.CriticalDamageMultiplier : 1);
        }

        return damage;
    }

    // Avanza el estado del jugador al final de la ronda, actualizando su salud, energía y contadores de bloqueos y ataques especiales.
    private PlayerState AdvancePlayer(
        PlayerState player,
        int health,
        int reserved,
        int blockadesUsedThisRound,
        bool usedSpecialAttackThisRound,
        bool isFinished)
    {
        return player with
        {
            Health = health,
            Energy = isFinished ? 0 : MatchRules.EnergyPerRound + reserved,
            ReservedEnergy = 0,
            UsedBlockades = player.UsedBlockades + blockadesUsedThisRound,
            HasUsedSpecialAttack = player.HasUsedSpecialAttack || usedSpecialAttackThisRound
        };
    }

    // Cuenta cuántas veces aparece una acción específica en un plan de ronda.
    private static int Count(RoundPlan plan, GameAction action) =>
        plan.Actions.Count(candidate => candidate == action);
}
