# Generates records.json, the data of the benchmark cases: jq -n -c -f records.gen.jq
def pick($xs; $k): $xs[$k % ($xs | length)];
[ range(2000) as $i
  | {
      id: $i,
      name: (pick(["Ada", "Grace", "Alan", "Edsger", "Barbara", "Donald", "Margaret", "Dennis", "Ken", "Linus", "Guido", "Bjarne"]; $i)
             + " " + pick(["Lovelace", "Hopper", "Turing", "Dijkstra", "Liskov", "Knuth", "Hamilton", "Ritchie", "Thompson", "Torvalds", "Rossum"]; $i * 7 + 3)),
      email: ("user\($i)@" + pick(["example.com", "example.org", "mail.test", "corp.example.net"]; $i * 5 + 1)),
      age: (18 + ($i * 37 % 60)),
      city: pick(["Buenos Aires", "Córdoba", "Rosario", "Mendoza", "La Plata", "Salta", "São Paulo", "Montevideo", "Santiago", "Lima", "Bogotá", "Quito", "München"]; $i * 11 + 5),
      active: ($i % 3 != 0),
      score: (($i * 7919 % 10007) / 100),
      tags: [range($i % 5) as $j | pick(["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta"]; $i + $j * 3)],
      address: {
        street: "\($i * 13 % 9000 + 1) Main St",
        zip: ("0000\($i * 97 % 100000)" | .[-5:]),
        geo: {lat: (-34.5 + ($i % 100) / 64), lon: (-58.25 - ($i % 77) / 32)}
      }
    }
]