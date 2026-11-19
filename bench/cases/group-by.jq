group_by(.city)
| map({
    city: .[0].city,
    count: length,
    avg_age: (map(.age) | add / length),
    top: (max_by(.score) | .name)
  })
| sort_by(-.count, .city)