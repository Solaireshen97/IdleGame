$ErrorActionPreference = 'Stop'
$origin = 'http://localhost:5115'
$auth = Invoke-RestMethod -Method Post -Uri "$origin/api/user/login" -ContentType 'application/json' -Body '{"userName":"CacheSmoke","password":"CacheSmoke123!"}'
$headers = @{ Authorization = 'Bearer ' + $auth.token }
$character = Invoke-RestMethod -Uri "$origin/api/user/character" -Headers $headers
$user = Invoke-RestMethod -Uri "$origin/api/user/me" -Headers $headers
if ($user.activeCharacterId -ne $character.characterId) { throw 'User balance identity mismatch' }
$roomId = $character.activeBattleRoomId
if (!$roomId) { throw 'Expected the isolated test room' }
$content = Invoke-WebRequest -Uri "$origin/api/content"
$etag = $content.Headers.ETag | Select-Object -First 1
$revalidation = Invoke-WebRequest -Uri "$origin/api/content" -Headers @{'If-None-Match'=$etag} -SkipHttpErrorCheck
if ($revalidation.StatusCode -ne 304) { throw 'Catalog revalidation must return 304' }
$first = Invoke-RestMethod -Method Post -Uri "$origin/api/battle/snapshot" -Headers $headers -ContentType 'application/json' -Body (@{roomId=$roomId} | ConvertTo-Json -Compress)
$owned = $first.room.slots | Where-Object characterId -eq $character.characterId | Select-Object -First 1
$originalSetting = [bool]$owned.isQuickSkillCastEnabled
$null = Invoke-RestMethod -Method Put -Uri "$origin/api/user/characters/$($character.characterId)/quick-skill-cast" -Headers $headers -ContentType 'application/json' -Body (@{isEnabled=!$originalSetting} | ConvertTo-Json -Compress)
$changed = Invoke-RestMethod -Method Post -Uri "$origin/api/battle/snapshot" -Headers $headers -ContentType 'application/json' -Body (@{roomId=$roomId; projectionId=$first.projectionId; historyEpoch=$first.room.battleHistoryEpoch; afterEventId=$first.lastEventId; afterLogId=$first.lastLogId} | ConvertTo-Json -Compress)
if ($null -eq $changed.room) { throw 'Direct preference update did not invalidate the projection' }
$changedSlot = $changed.room.slots | Where-Object characterId -eq $character.characterId | Select-Object -First 1
if ([bool]$changedSlot.isQuickSkillCastEnabled -eq $originalSetting) { throw 'Projection did not include updated preference' }
$null = Invoke-RestMethod -Method Put -Uri "$origin/api/user/characters/$($character.characterId)/quick-skill-cast" -Headers $headers -ContentType 'application/json' -Body (@{isEnabled=$originalSetting} | ConvertTo-Json -Compress)
$unauthorized = Invoke-WebRequest -Method Post -Uri "$origin/api/battle/snapshot" -ContentType 'application/json' -Body (@{roomId=$roomId} | ConvertTo-Json -Compress) -SkipHttpErrorCheck
if ($unauthorized.StatusCode -ne 401) { throw 'Unauthenticated synchronization was not rejected' }
$samples = [System.Collections.Generic.List[object]]::new()
$cursor = @{roomId=$roomId}
$duration = [Diagnostics.Stopwatch]::StartNew()
for ($poll = 0; $poll -lt 30; $poll++) {
    $delay = $poll * 2000 - $duration.ElapsedMilliseconds
    if ($delay -gt 0) { Start-Sleep -Milliseconds $delay }
    $requestTime = [Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-WebRequest -Method Post -Uri "$origin/api/battle/snapshot" -Headers $headers -ContentType 'application/json' -Body ($cursor | ConvertTo-Json -Compress)
    $requestTime.Stop()
    $state = $response.Content | ConvertFrom-Json
    $view = if ($null -ne $state.room) { $state.room } else { $state.unchanged }
    if ($null -eq $view -or $view.roomId -ne $roomId) { throw 'Missing synchronized room state' }
    $samples.Add([pscustomobject]@{route='snapshot'; elapsedMs=$requestTime.Elapsed.TotalMilliseconds; bytes=[Text.Encoding]::UTF8.GetByteCount($response.Content); full=$null -ne $state.room})
    $cursor = @{roomId=$roomId; projectionId=$state.projectionId; historyEpoch=$view.battleHistoryEpoch; afterEventId=$state.lastEventId; afterLogId=$state.lastLogId}
    if ($poll % 5 -eq 0) {
        foreach ($route in @('character','me')) {
            $requestTime.Restart()
            $summary = Invoke-WebRequest -Uri "$origin/api/user/$route" -Headers $headers
            $requestTime.Stop()
            $samples.Add([pscustomobject]@{route=$route; elapsedMs=$requestTime.Elapsed.TotalMilliseconds; bytes=[Text.Encoding]::UTF8.GetByteCount($summary.Content); full=$false})
        }
    }
}
$remaining = 60000 - $duration.ElapsedMilliseconds
if ($remaining -gt 0) { Start-Sleep -Milliseconds $remaining }
$times = @($samples | Sort-Object elapsedMs | Select-Object -ExpandProperty elapsedMs)
[pscustomobject]@{
    durationSeconds=[Math]::Round($duration.Elapsed.TotalSeconds,2)
    scenario='One-minute HTTP cadence simulation: battle every 2s and character/user summaries every 10s; isolated SQLite, idle private one-character room, no browser network trace'
    requests=$samples.Count
    snapshotRequests=@($samples | Where-Object route -eq 'snapshot').Count
    fullSnapshots=@($samples | Where-Object { $_.route -eq 'snapshot' -and $_.full }).Count
    unchangedSnapshots=@($samples | Where-Object { $_.route -eq 'snapshot' -and !$_.full }).Count
    responseUtf8Bytes=($samples | Measure-Object bytes -Sum).Sum
    p50Ms=[Math]::Round($times[[int]($times.Count*.5)],2)
    p95Ms=[Math]::Round($times[[Math]::Ceiling($times.Count*.95)-1],2)
    catalogRevalidationStatus=$revalidation.StatusCode
    preferenceInvalidationVerified=$true
    unauthorizedSnapshotStatus=$unauthorized.StatusCode
} | ConvertTo-Json | Tee-Object -FilePath .codex-tmp/query-cache-minute-result.json
